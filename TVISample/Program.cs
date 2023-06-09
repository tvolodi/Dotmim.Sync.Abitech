﻿// See https://aka.ms/new-console-template for more information
using Dotmim.Sync;
using Dotmim.Sync.MySql;
using Dotmim.Sync.Sqlite;

Console.WriteLine("Started!");


MySqlSyncProvider serverProvider = new MySqlSyncProvider(
    @"Server=127.0.0.1;Port=3306;Database=AdventureWorks;Uid=root;Pwd=Vol&Work#1;GuidFormat=Binary16");

// Sqlite Client provider acting as the "client"
SqliteSyncProvider clientProvider = new SqliteSyncProvider("advworks.db");

// Tables involved in the sync process:
var setup = new SyncSetup("Address"
    //, "ProductCategory", "ProductDescription", "ProductModel",
    //                      "Product",
    //                      "Customer", "CustomerAddress", "SalesOrderHeader", "SalesOrderDetail"
                          );

// Sync agent
SyncAgent agent = new SyncAgent(clientProvider, serverProvider);

do
{
    var result = await agent.SynchronizeAsync(setup);
    Console.WriteLine(result);

} while (Console.ReadKey().Key != ConsoleKey.Escape);