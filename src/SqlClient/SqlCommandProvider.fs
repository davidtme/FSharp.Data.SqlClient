﻿namespace FSharp.Data

open System
open System.Data
open System.IO
open System.Data.SqlClient
open System.Reflection
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Configuration
open System.Runtime.Caching

open Microsoft.SqlServer.Server

open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations

open FSharp.Data.SqlClient

open ProviderImplementation.ProvidedTypes
open System.Text.RegularExpressions

[<assembly:TypeProviderAssembly()>]
#if DEBUG
[<assembly:InternalsVisibleTo("SqlClient.Tests")>]
#endif
do()

[<TypeProvider>]
[<CompilerMessageAttribute("This API supports the FSharp.Data.SqlClient infrastructure and is not intended to be used directly from your code.", 101, IsHidden = true)>]
type SqlCommandProvider(config : TypeProviderConfig) as this = 
    inherit TypeProviderForNamespaces()

    let nameSpace = this.GetType().Namespace
    let assembly = Assembly.LoadFrom( config.RuntimeAssembly)
    let providerType = ProvidedTypeDefinition(assembly, nameSpace, "SqlCommandProvider", Some typeof<obj>, HideObjectMethods = true)

    let cache = new MemoryCache(name = this.GetType().Name)

    do 
        this.Disposing.Add <| fun _ ->
            try  
                cache.Dispose()
                clearDataTypesMap()
            with _ -> ()

    do 
        providerType.DefineStaticParameters(
            parameters = [ 
                ProvidedStaticParameter("CommandText", typeof<string>) 
                ProvidedStaticParameter("ConnectionStringOrName", typeof<string>) 
                ProvidedStaticParameter("ResultType", typeof<ResultType>, ResultType.Records) 
                ProvidedStaticParameter("SingleRow", typeof<bool>, false)   
                ProvidedStaticParameter("ConfigFile", typeof<string>, "") 
                ProvidedStaticParameter("AllParametersOptional", typeof<bool>, false) 
                ProvidedStaticParameter("DataDirectory", typeof<string>, "") 
                ProvidedStaticParameter("TempTableDefinitions", typeof<string>, "") 
                ProvidedStaticParameter("TableVarMapping", typeof<string>, "") 
            ],             
            instantiationFunction = (fun typeName args ->
                let value = lazy this.CreateRootType(typeName, unbox args.[0], unbox args.[1], unbox args.[2], unbox args.[3], unbox args.[4], unbox args.[5], unbox args.[6], unbox args.[7], unbox args.[8])
                cache.GetOrAdd(typeName, value)
            ) 
        )

        providerType.AddXmlDoc """
<summary>Typed representation of a T-SQL statement to execute against a SQL Server database.</summary> 
<param name='CommandText'>Transact-SQL statement to execute at the data source.</param>
<param name='ConnectionStringOrName'>String used to open a SQL Server database or the name of the connection string in the configuration file in the form of “name=&lt;connection string name&gt;”.</param>
<param name='ResultType'>A value that defines structure of result: Records, Tuples, DataTable, or SqlDataReader.</param>
<param name='SingleRow'>If set the query is expected to return a single row of the result set. See MSDN documentation for details on CommandBehavior.SingleRow.</param>
<param name='ConfigFile'>The name of the configuration file that’s used for connection strings at DESIGN-TIME. The default value is app.config or web.config.</param>
<param name='AllParametersOptional'>If set all parameters become optional. NULL input values must be handled inside T-SQL.</param>
<param name='ResolutionFolder'>A folder to be used to resolve relative file paths to *.sql script files at compile time. The default value is the folder that contains the project or script.</param>
<param name='DataDirectory'>The name of the data directory that replaces |DataDirectory| in connection strings. The default value is the project or script directory.</param>
<param name='TempTableDefinitions'>Temp tables create command.</param>
<param name='TableVarMapping'>List table-valued parameters in the format of "@tvp1=[dbo].[TVP_IDs]; @tvp2=[dbo].[TVP_IDs]"</param>
"""

        this.AddNamespace(nameSpace, [ providerType ])

    override this.ResolveAssembly args = 
        config.ReferencedAssemblies 
        |> Array.tryFind (fun x -> AssemblyName.ReferenceMatchesDefinition(AssemblyName.GetAssemblyName x, AssemblyName args.Name)) 
        |> Option.map Assembly.LoadFrom
        |> defaultArg 
        <| base.ResolveAssembly args

    member internal this.CreateRootType(typeName, sqlStatement, connectionStringOrName: string, resultType, singleRow, configFile, allParametersOptional, dataDirectory, tempTableDefinitions, tableVarMapping) = 

        if singleRow && not (resultType = ResultType.Records || resultType = ResultType.Tuples)
        then 
            invalidArg "singleRow" "SingleRow can be set only for ResultType.Records or ResultType.Tuples."
        
        if connectionStringOrName.Trim() = ""
        then invalidArg "ConnectionStringOrName" "Value is empty!" 

        let designTimeConnectionString = DesignTimeConnectionString.Parse(connectionStringOrName, config.ResolutionFolder, configFile)

        let dataDirectoryFullPath = 
            if dataDirectory = "" then  config.ResolutionFolder
            elif Path.IsPathRooted dataDirectory then dataDirectory
            else Path.Combine (config.ResolutionFolder, dataDirectory)

        AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectoryFullPath)

        let conn = new SqlConnection(designTimeConnectionString.Value)
        use closeConn = conn.UseLocally()
        conn.CheckVersion()
        conn.LoadDataTypesMap()

        let designTimeSqlStatement, tempTableNames = 
            let sql, tempTableNames = DesignTime.SubstituteTempTables(conn, sqlStatement, tempTableDefinitions)
            DesignTime.SubstituteTableVar(sql, tableVarMapping), tempTableNames

        let parameters = DesignTime.ExtractParameters(conn, designTimeSqlStatement, allParametersOptional)

        let outputColumns = 
            if resultType <> ResultType.DataReader
            then DesignTime.GetOutputColumns(conn, designTimeSqlStatement, parameters, isStoredProcedure = false)
            else []

        DesignTime.RemoveSubstitutedTempTables(conn, tempTableNames)
            
        let rank = if singleRow then ResultRank.SingleRow else ResultRank.Sequence
        let returnType = DesignTime.GetOutputTypes(outputColumns, resultType, rank, hasOutputParameters = false)
        
        let cmdProvidedType = ProvidedTypeDefinition(assembly, nameSpace, typeName, Some typeof<``ISqlCommand Implementation``>, HideObjectMethods = true)

        do  
            cmdProvidedType.AddMember(ProvidedProperty("ConnectionStringOrName", typeof<string>, [], IsStatic = true, GetterCode = fun _ -> <@@ connectionStringOrName @@>))

        do
            SharedLogic.alterReturnTypeAccordingToResultType returnType cmdProvidedType resultType

        do  //ctors
            let designTimeConfig = 
                let expectedDataReaderColumns = 
                    Expr.NewArray(
                        typeof<string * string>, 
                        [ for c in outputColumns -> Expr.NewTuple [ Expr.Value c.Name; Expr.Value c.TypeInfo.ClrTypeFullName ] ]
                    )

                <@@ {
                    SqlStatement = sqlStatement
                    IsStoredProcedure = false
                    Parameters = %%Expr.NewArray( typeof<SqlParameter>, parameters |> List.map QuotationsFactory.ToSqlParam)
                    ResultType = resultType
                    Rank = rank
                    RowMapping = %%returnType.RowMapping
                    ItemTypeName = %%returnType.RowTypeName
                    ExpectedDataReaderColumns = %%expectedDataReaderColumns
                } @@>

            do
                DesignTime.GetCommandCtors(
                    cmdProvidedType, 
                    designTimeConfig, 
                    designTimeConnectionString, 
                    config.IsHostedExecution,
                    factoryMethodName = "Create"
                )
                |> cmdProvidedType.AddMembers

        do  //AsyncExecute, Execute, and ToTraceString

            let executeArgs = DesignTime.GetExecuteArgs(cmdProvidedType, parameters, udttsPerSchema = null)

            let hasOutputParameters = false
            let addRedirectToISqlCommandMethod outputType name = 
                DesignTime.AddGeneratedMethod(parameters, hasOutputParameters, executeArgs, cmdProvidedType.BaseType, outputType, name) 
                |> cmdProvidedType.AddMember

            addRedirectToISqlCommandMethod returnType.Single "Execute" 
                            
            let asyncReturnType = ProvidedTypeBuilder.MakeGenericType(typedefof<_ Async>, [ returnType.Single ])
            addRedirectToISqlCommandMethod asyncReturnType "AsyncExecute" 

            addRedirectToISqlCommandMethod typeof<string> "ToTraceString" 

        cmdProvidedType
