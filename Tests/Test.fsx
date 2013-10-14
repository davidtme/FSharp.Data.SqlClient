﻿
#r "../SqlCommandTypeProvider/bin/Debug/SqlCommandTypeProvider.dll"

open FSharp.Data.SqlClient
open System.Data
open System

[<Literal>]
let connectionString="Data Source=.;Initial Catalog=AdventureWorks2012;Integrated Security=True"
//let connectionString="Server=tcp:ybxsjdodsy.database.windows.net,1433;Database=AdventureWorks2012;User ID=stewie@ybxsjdodsy;Password=Leningrad1;Trusted_Connection=False;Encrypt=True;Connection Timeout=30;"
//let connectionString="Server=tcp:lhwp7zue01.database.windows.net,1433;Database=AdventureWorks2012;User ID=jackofshadows@lhwp7zue01;Password=Leningrad1;Trusted_Connection=False;Encrypt=True;Connection Timeout=30;"

[<Literal>]
let queryProductsSql = " 
SELECT TOP (@top) Name AS ProductName, SellStartDate
FROM Production.Product 
WHERE SellStartDate > @SellStartDate
"

//Tuples
type QueryProductsAsTuples = SqlCommand<queryProductsSql, connectionString>
let cmd = QueryProductsAsTuples(top = 7L, SellStartDate = System.DateTime.Parse "2002-06-01")
let result : Async<(string * DateTime) seq> = cmd.Execute()
result 
    |> Async.RunSynchronously 
    |> Seq.iter (fun(productName, sellStartDate) -> printfn "Product name: %s. Sells start date %A" productName sellStartDate)

//Custom record types
type QueryProducts = SqlCommand<queryProductsSql, connectionString, ResultType = ResultType.Records>
let cmd1 = QueryProducts(top = 7L, SellStartDate = System.DateTime.Parse "2002-06-01")
let result1 : Async<QueryProducts.Record seq> = cmd1.Execute()
result1 
    |> Async.RunSynchronously 
    |> Seq.iter (fun x -> printfn "Product name: %s. Sells start date %A" x.ProductName x.SellStartDate)

//DataTable for data binding scenarios and update
type QueryProductDataTable = SqlCommand<queryProductsSql, connectionString, ResultType = ResultType.DataTable>
let cmd2 = QueryProductDataTable(top = 7L, SellStartDate = System.DateTime.Parse "2002-06-01")
let result2 : Async<DataTable<QueryProductDataTable.Row>> = cmd2.Execute() 
result2 
    |> Async.RunSynchronously 
    |> Seq.map (fun row -> printfn "Product name: %s. Sells start date %O" row.ProductName row.SellStartDate)

//Single row hint
type QueryPersonInfoSingletone = SqlCommand<"SELECT * FROM dbo.ufnGetContactInformation(@PersonId)", connectionString, ResultType = ResultType.Records, SingleRow=true>
let cmd3 = new QueryPersonInfoSingletone(PersonId = 2)
let result3 : Async<QueryPersonInfoSingletone.Record> = cmd3.Execute() 
result3 
    |> Async.RunSynchronously 
    |> fun x -> printfn "Person info: Id - %i, FirstName - %s, LastName - %s, JobTitle - %s, BusinessEntityType - %s" x.PersonID x.FirstName x.LastName x.JobTitle x.BusinessEntityType

//Non-query
type UpdateEmplInfoCommand = SqlCommand<"EXEC HumanResources.uspUpdateEmployeePersonalInfo @BusinessEntityID, @NationalIDNumber, @BirthDate, @MaritalStatus, @Gender", connectionString>
let cmd4 = new UpdateEmplInfoCommand(BusinessEntityID = 2, NationalIDNumber = "245797967", BirthDate = System.DateTime(1965, 09, 01), MaritalStatus = "S", Gender = "M")
let result4 : Async<int> = cmd4.Execute() 
let rowsAffected = result4 |> Async.RunSynchronously 

//Single value
type GetServerTime = SqlCommand<"IF @IsUtc = CAST(1 AS BIT) SELECT GETUTCDATE() ELSE SELECT GETDATE()", connectionString, SingleRow=true>
let getSrvTime = new GetServerTime(IsUtc = true)
let result5 : Async<DateTime> = getSrvTime.Execute()
result5 |> Async.RunSynchronously |> printfn "%A"
getSrvTime.IsUtc <- false
//Execute again
getSrvTime.Execute() |> Async.RunSynchronously |> printfn "%A"

//Stored procedure by name only
type UpdateEmplInfoCommandSp = SqlCommand<"HumanResources.uspUpdateEmployeePersonalInfo", connectionString, CommandType = CommandType.StoredProcedure>
let cmdSp = new UpdateEmplInfoCommandSp(BusinessEntityID = 2, NationalIDNumber = "245797967", BirthDate = System.DateTime(1965, 09, 01), MaritalStatus = "S", Gender = "F")
cmdSp.Execute() |> Async.RunSynchronously
cmdSp.SpReturnValue


