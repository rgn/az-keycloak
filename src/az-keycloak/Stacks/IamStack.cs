using Pulumi;
using AzureNative = Pulumi.AzureNative;
using Random = Pulumi.Random;
using System;
using Microsoft.Data.SqlClient;
using System.Net.Http;
using System.Threading.Tasks;
using Pulumi.AzureNative.Web;

namespace azkeycloak;

internal class IamStack : Stack
{
    #region Config

    // instance of <see cref="Config" /> to read Pulumi configuration.
    private readonly Config _config = new();

    // name used for resource group & sql server
    private string _name => _config.Get("name") ?? "demo";

    // get the admin user from the configuration, if not present assume `sqladm`.
    private string _sqlAdminUser => _config.Get("sql.admin.user") ?? "sqladm";

    // get the keycloak user from the configuration, if not present assume `kcadm`. 
    private string _keycloakDbUser => _config.Get("sql.kc.user") ?? "kcadm";

    // region to place our resources, if not present use `westeurope`.
    private string _region  => _config.Get("location") ?? "westeurope";

    #endregion Config

    #region Outputs

    // will hold the sql admin password, after it was created.
    [Output] public Output<string> SqlAdminPassword { get; set; } = default!;
    
    // will hold the sql keycloak password, after it was created.
    [Output] public Output<string> KeycloakDbPassword { get; set; } = default!;

    #endregion Outputs

    #region ResourceReferences

    // reference to the resource group
    private readonly AzureNative.Resources.ResourceGroup _resourceGroup = default!;

    // reference to the sql server
    private readonly AzureNative.Sql.Server _sqlServer = default!;

    // reference to the sql database
    private readonly AzureNative.Sql.Database _sqlDatabase = default!;

    #endregion Resourcereferences

    /// <summary>
    /// CTOR
    /// </summary>
    public IamStack()
    {        
        _resourceGroup = AddResourceGroup(_name, _region);
        _sqlServer = AddSqlServer(_name);
        _sqlDatabase = AddSqlDatabase("keycloak");

        // generate the password for the sql admin user
        KeycloakDbPassword = new Random.RandomPassword("password-sql-kc", new()
        {
            Length = 16,
            Special = true,
            OverrideSpecial = "!#$%&*()-_=+[]{}<>:?",
        }).Result;
        
        SetupSqlDbLogin(_keycloakDbUser, KeycloakDbPassword);        
    }

    #region ResourceGroup
    
    /// <summary>
    /// Create a Azure Resource Group.
    /// </summary>
    /// <param name="name">Resource Group name</param>
    /// <param name="region">Azure region</param>
    /// <returns><see cref="AzureNative.Resources.ResourceGroup"/></returns>
    private AzureNative.Resources.ResourceGroup AddResourceGroup(string name, string region)
        => new AzureNative.Resources.ResourceGroup("resource-group", new()
        {
            ResourceGroupName = $"rg-{name}-{region}-1",
            Location = region
        });
    
    #endregion ResourceGroup

    #region SqlServer
    /// <summary>
    /// Create Azure Sql Server.
    /// </summary>
    /// <param name="name">Server name.</param>
    /// <returns><see cref="AzureNative.Sql.Server"/></returns>
    private AzureNative.Sql.Server AddSqlServer(string name)
    {
        // generate the password for the sql admin user
        SqlAdminPassword = new Random.RandomPassword("password-sql-admin", new()
        {
            Length = 16,
            Special = true,
            OverrideSpecial = "!#$%&*()-_=+[]{}<>:?",
        }).Result;

        var sqlServer = new AzureNative.Sql.Server($"sql-server-{name}", new()
        {
            ServerName = _resourceGroup.Location.Apply(location => $"sql-{name}-{location}-1"),
            ResourceGroupName = _resourceGroup.Name,
            Location = _resourceGroup.Location,
            AdministratorLogin = _sqlAdminUser,
            AdministratorLoginPassword = SqlAdminPassword.Apply(pwd => $"{pwd}"),
            MinimalTlsVersion = "1.2",
            Version = "12.0",
            PublicNetworkAccess = AzureNative.Sql.ServerNetworkAccessFlag.Enabled,
            RestrictOutboundNetworkAccess = AzureNative.Sql.ServerNetworkAccessFlag.Disabled
        }, new CustomResourceOptions
        {
            DependsOn = { _resourceGroup }
        });

        // Allow connection to SQL Server from other Azure Services, wait fore sqlServer to be ready
        var azureFirewallRule = new AzureNative.Sql.FirewallRule("fw-rule-public", new()
        {
            FirewallRuleName = "allowAllWindowsAzureIps",
            ResourceGroupName = _resourceGroup.Name,
            ServerName = sqlServer.Name,
            StartIpAddress = "0.0.0.0",
            EndIpAddress = "0.0.0.0",
        }, new CustomResourceOptions
        {
            DependsOn = { sqlServer }
        });

        // Allow connection to SQL Server via internet for the current IP
        var myIp = GetPublicIp().GetAwaiter().GetResult();
        var myFirewallRule = new AzureNative.Sql.FirewallRule("fw-rule-my-ip", new()
        {
            FirewallRuleName = "allowMyIp",
            ResourceGroupName = _resourceGroup.Name,
            ServerName = sqlServer.Name,
            StartIpAddress = myIp,
            EndIpAddress = myIp,
        }, new CustomResourceOptions
        {
            DependsOn = { sqlServer }
        });

        return sqlServer;
    }
    #endregion SqlServer

    #region SqlDatabase
    /// <summary>
    /// Create a SQL database. Defaults are choosen to cut costs as mutch as possible.
    /// </summary>
    /// <param name="name">Database name.</param>
    /// <param name="capacity">Allocated capacity. Default = 5.</param>
    /// <param name="tier">Tier to use. Default = Basic</param>
    /// <returns><see cref="AzureNative.Sql.Database"/></returns>
    private AzureNative.Sql.Database AddSqlDatabase(string name, int capacity = 5, string tier = "Basic")
        => new AzureNative.Sql.Database($"sql-db-{name}", new()
        {
            DatabaseName = name,
            ResourceGroupName = _resourceGroup.Name,
            Location = _resourceGroup.Location,
            ServerName = _sqlServer.Name,
            ZoneRedundant = false,            
            Sku = new AzureNative.Sql.Inputs.SkuArgs
            {
                Capacity = capacity,
                Name = tier,
                Tier = tier
            }
        }, new CustomResourceOptions
        {
            DependsOn =  { _resourceGroup, _sqlServer }
        });
    #endregion SqlDatabase

    #region SqlDbUser
    /// <summary>
    /// Create a Sql Server login, db user and use the given password.
    /// </summary>
    /// <param name="login">Login name.</param>
    /// <param name="password">Generated password.</param>
    private void SetupSqlDbLogin(string login, Output<string> password)
    {
        Output.All(_sqlServer.Name, _sqlDatabase.Name, SqlAdminPassword, password).Apply(x => {

            var sqlAdminPassword = x[2];
            var userPassword = x[3];

            var connectionStringBuilderMaster = new SqlConnectionStringBuilder();
            connectionStringBuilderMaster.DataSource = $"{x[0]}.database.windows.net,1433";
            connectionStringBuilderMaster.InitialCatalog = "master";
            connectionStringBuilderMaster.UserID= _sqlAdminUser;
            connectionStringBuilderMaster.Password = sqlAdminPassword;
            connectionStringBuilderMaster.TrustServerCertificate = true;
            connectionStringBuilderMaster.Encrypt = true;
            connectionStringBuilderMaster.HostNameInCertificate = "*.database.windows.net";
            connectionStringBuilderMaster.ConnectTimeout = 30;
            
            try {
                using (var conn = new SqlConnection(connectionStringBuilderMaster.ConnectionString))
                {
                    conn.Open();
                    new SqlCommand(@$"
                    IF NOT EXISTS (SELECT * FROM [sys].[sql_logins] WHERE NAME = '{login}') 
                        CREATE LOGIN [{login}] WITH password='{userPassword}';
                    ", conn).ExecuteNonQuery();
                }
                
                var connectionStringBuilderKeycloak = new SqlConnectionStringBuilder();
                connectionStringBuilderKeycloak.DataSource = $"{x[0]}.database.windows.net,1433";
                connectionStringBuilderKeycloak.InitialCatalog = x[1];
                connectionStringBuilderKeycloak.UserID= _sqlAdminUser;
                connectionStringBuilderKeycloak.Password = sqlAdminPassword;
                connectionStringBuilderKeycloak.TrustServerCertificate = true;
                connectionStringBuilderKeycloak.Encrypt = true;
                connectionStringBuilderKeycloak.HostNameInCertificate = "*.database.windows.net";
                connectionStringBuilderKeycloak.ConnectTimeout = 30;
                using (var conn = new SqlConnection(connectionStringBuilderKeycloak.ConnectionString))
                {
                    conn.Open();
                    new SqlCommand(@$"
                        IF NOT EXISTS (SELECT * FROM [sys].[sysusers] WHERE NAME = '{login}') 
                            CREATE USER [{login}] FROM LOGIN [{login}];
                            EXEC sp_addrolemember 'db_owner', '{login}';
                    ", conn).ExecuteNonQuery();
                }
                
            }
            catch (Exception ex)
            {
                System.Console.WriteLine(ex.Message);

                throw;
            }

            return true;
        });
    }
    #endregion SqlDbUser


    #region Utils
    /// <summary>
    /// Get your current public IP.
    /// </summary>
    /// <param name="serviceUrl">Service to use. Default = https://ipinfo.io</param>
    /// <param name="path">Relative path to get the ip. Default = ip</param>
    /// <returns></returns>
    private static async Task<string> GetPublicIp(string serviceUrl = "https://ipinfo.io", string path = "ip")
    {
        using var httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(serviceUrl);
        
        return await httpClient.GetStringAsync(path);
    }
    #endregion Utils
}