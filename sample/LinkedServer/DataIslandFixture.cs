﻿using System.Data;
using DataIsland;
using DataIsland.EfCore;
using DataIsland.SqlServer;
using LinkedServer;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(DataIslandFixture))]

namespace LinkedServer;

public class DataIslandFixture : IAsyncLifetime
{
    private const string LinkedServerName = "OtherServer";
    private readonly MsSqlContainer _mainServer;
    private readonly MsSqlContainer _linkedServer;

    public DataIslandFixture()
    {
        _mainServer = new MsSqlBuilder().Build();
        _linkedServer = new MsSqlBuilder().Build();

        var sqlServerPool = SqlServerComponentFactory.Docker(_mainServer, _linkedServer);
        var factory = new SqlDatabaseTenantFactory("/tmp");
        Island = new DataIslandBuilder()
            .AddComponentPool(sqlServerPool, factory)
            .AddTemplate("default", template =>
            {
                template.AddDataAccess<AppDbContext>("App db");
                template.AddTenant<SqlDatabaseTenant, SqlDatabaseSpec>("App db", "App server");
                template.AddComponent<SqlServerComponent, SqlServerSpec>(
                    "App server",
                    spec => spec
                        .WithLinkedServerName(LinkedServerName));

                template.AddDataAccess<LinkedDbContext>("Linked db");
                template.AddTenant<SqlDatabaseTenant, SqlDatabaseSpec>("Linked db", "Linked server");
                template.AddComponent<SqlServerComponent, SqlServerSpec>("Linked server");

                template.AddAfterInit(async (tenants, ct) =>
                {
                    var mainDatabase = (SqlDatabaseTenant)tenants
                        .Single(x => x.TenantName == "App db")
                        .Instance;
                    var linkedDb = (SqlDatabaseTenant)tenants
                        .Single(x => x.TenantName == "Linked db")
                        .Instance;

                    // This is the database on the linked server.
                    await using var connection = new SqlConnection(mainDatabase.ConnectionString);
                    await connection.OpenAsync(ct);

                    // Create the synonym. Synonym is database scoped object, thus each tenant can have
                    // its own and not interfere with others tenants (=tests).
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"""
                        CREATE SYNONYM [LinkedUsers]
                            FOR [{LinkedServerName}].[{linkedDb.DatabaseName}].[dbo].[Users]
                        """;
                    await command.ExecuteNonQueryAsync(ct);
                });
            })
            .AddPatcher(new EfCorePatcher<AppDbContext>())
            .AddPatcher(new EfCorePatcher<LinkedDbContext>())
            .Build();
    }

    internal IDataIsland Island { get; }

    public async ValueTask InitializeAsync()
    {
        await Island.InitializeAsync();

        // Because linked server is a component configuration, it should be done before any test
        // is executed. This is thus ideal place, assembly fixture is executed before tests and
        // is async, thus we can use async methods.
        await ConnectToLinkedServerAsync(_mainServer, _linkedServer);
    }

    public async ValueTask DisposeAsync()
    {
        await Island.DisposeAsync();
    }

    private static async Task ConnectToLinkedServerAsync(MsSqlContainer sqlServer, MsSqlContainer linkedServer)
    {
        var connectionString = sqlServer.GetConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Docker by default connects created containers to same network that does allow
        // communicating through IP, but not hostnames. For testing, we generally don't care
        // about security, but if the tests use sensitive data (even anonymized) a separate
        // docker network is potentially a better choice.
        var linkedServerDockerDataSource = linkedServer.IpAddress + "," + MsSqlBuilder.MsSqlPort;
        await AddLinkedServerAsync(connection, linkedServerDockerDataSource);

        // Set credentials for linked server.
        await AddLinkedServerLoginAsync(connection);

        // Make sure server can connect and get data through linked server.
        await TestLinkedServerAsync(connection);
        return;

        static async Task AddLinkedServerAsync(SqlConnection connection, string dataSource)
        {
            var command = connection.CreateCommand();
            command.CommandText = "sp_addlinkedserver";
            command.CommandType = CommandType.StoredProcedure;
            command.Parameters.AddWithValue("@server", LinkedServerName);
            command.Parameters.AddWithValue("@srvproduct", "");
            command.Parameters.AddWithValue("@provider", "SQLNCLI");
            command.Parameters.AddWithValue("@datasrc", dataSource);
            await command.ExecuteNonQueryAsync();
        }

        static async Task AddLinkedServerLoginAsync(SqlConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = "sp_addlinkedsrvlogin";
            command.CommandType = CommandType.StoredProcedure;
            command.Parameters.AddWithValue("@rmtsrvname", LinkedServerName);
            command.Parameters.AddWithValue("@useself", "FALSE");
            command.Parameters.AddWithValue("@locallogin", DBNull.Value);
            command.Parameters.AddWithValue("@rmtuser", MsSqlBuilder.DefaultUsername);
            command.Parameters.AddWithValue("@rmtpassword", MsSqlBuilder.DefaultPassword);
            await command.ExecuteNonQueryAsync();
        }

        static async Task TestLinkedServerAsync(SqlConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = "sp_testlinkedserver";
            command.CommandType = CommandType.StoredProcedure;
            command.Parameters.AddWithValue("@servername", LinkedServerName);
            await command.ExecuteNonQueryAsync();
        }
    }
}
