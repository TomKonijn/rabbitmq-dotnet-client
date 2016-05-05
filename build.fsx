#r "./packages/FAKE/tools/FakeLib.dll"
#r "System.Xml.Linq"
open Fake
open System
open System.Xml.Linq
open System.Diagnostics


let (|IsWin8OrHigher|_|) (ov: OperatingSystem) =
    if ov.Platform = PlatformID.Win32NT && ov.Version > Version("6.2") then
        Some ()
    else None

type BuildEnv =
    | AppVeyor 
    | Mono
    | Windows
    | Windows8Plus

let getEnv = Environment.GetEnvironmentVariable

Environment.CurrentDirectory <- __SOURCE_DIRECTORY__

let gitBranch = Fake.Git.Information.getBranchName __SOURCE_DIRECTORY__

let targetFramework = "v4.5"

let context =
    let osVersion = Environment.OSVersion
    match getEnv "APPVEYOR_BUILD_VERSION" with
    | null -> 
        let buildEnv =
            match osVersion with
            | IsWin8OrHigher _ -> Windows8Plus
            | _ when isMono -> Mono
            | _ -> Windows
        match getEnv "RABBIT_VSN" with
        | null -> 
            tracefn "warn: no version environment variable specificed -defaulting to 0.0.0.0" 
            buildEnv, "0.0.0.0"
        | v -> buildEnv, v
    | v ->
        AppVeyor, v


tracefn "Fake: context %A" context

let buildEnv, version = context

let (</>) a b = IO.Path.Combine(a, b)

let localPropsFile = "./Local.props"
let projects =
    [ "RabbitMQ.Client" ; "RabbitMQ.Client.WinRT" ]
let apiGenExe = "./gensrc/rabbitmq-dotnet-apigen.exe"
let specsDir = "./docs/specs"
let amqpSpec0_9_1BaseName = "amqp0-9-1.stripped.xml"
let amqpSpec0_9BaseName = "amqp0-9.stripped.xml"
let amqpSpec0_9_1i = specsDir </> amqpSpec0_9_1BaseName
let autogeneratedApi0_9_1BaseName = "autogenerated-api-0-9-1.cs"

let createGensrcDirs () =
    projects
    |> List.map (fun p -> "./gensrc" </> p)
    |> List.map IO.Directory.CreateDirectory 
    |> ignore

let deleteGensrcDirs () =
    projects
    |> List.map (fun p -> "./gensrc" </> p)
    |> List.map (fun p ->
        if IO.Directory.Exists p then IO.Directory.Delete(p, true))
    |> ignore

let generateApi () = 
    projects
    |> List.map (fun p -> "./gensrc/" </> p)
    |> List.iter (fun p ->
        let autogeneratedApi0_9_1 = p </> autogeneratedApi0_9_1BaseName
        ExecProcess 
            (fun si -> 
                    si.FileName <- apiGenExe
                    si.Arguments <-  "/apiName:AMQP_0_9_1 " + amqpSpec0_9_1i + " " + autogeneratedApi0_9_1) 
            (TimeSpan.FromMinutes 1.)
        |> fun res -> if res <> 0 then failwith "generateApi process failed")
    
let assemblyInfos = !!(@"./projects/**/AssemblyInfo.cs") 
                       --(@"**\*Scripts*\**")

let appRefs = 
    let main =
        !! "./projects/client/RabbitMQ.Client/**/*.csproj" 
        ++ "./projects/client/Unit/**/*.csproj" 
        ++ "./projects/wcf/**/*.csproj" 
    match buildEnv with
    | Mono -> main
    | _ -> 
        !! "./projects/**/*.csproj" 

let appGenRef = [ "./projects/client/Apigen/RabbitMQ.Client.Apigen.csproj" ] 

Target "BuildApigen" (fun _ ->
    MSBuildRelease "./gensrc" "Build" appGenRef
    |> Log "Build: ")

Target "BuildClient" (fun _ ->
    //MSBuildWithDefaults "Build" appRefs 
    MSBuild null "Build" ["Configuration", "Release"; "Platform", "AnyCPU" ] appRefs 
    |> Log "Build: ")

Target "Clean" (fun _ ->
    deleteGensrcDirs()
    MSBuild null "Clean" ["Configuration", "Release"; "Platform", "AnyCPU" ] appRefs 
    //MSBuildRelease null "Clean" appRefs 
    |> Log "Clean: ")

Target "UpdateAssemblyInfos"
    (fun _ ->
        trace (sprintf "Updating assembly infos to: %s" version)
        ReplaceAssemblyInfoVersionsBulk assemblyInfos (fun f -> 
            { f with AssemblyVersion = version }))    
        
Target "Test" (fun _ ->  
    !! ("./projects/client/**/build/**/unit-tests.dll")
    |> NUnit (fun p -> 
        { p with
            TimeOut = TimeSpan.FromMinutes 30.
            DisableShadowCopy = true; 
            OutputFile = "./TestResults.xml"}))

    
Target "AppVeyorTest" (fun _ ->  
    !! ("./projects/client/**/build/**/unit-tests.dll")
    |> NUnit (fun p -> 
        { p with
            TimeOut = TimeSpan.FromMinutes 30.
            ExcludeCategory = "RequireSMP,LongRunning,GCTest"
            DisableShadowCopy = true; 
            OutputFile = "./TestResults.xml"}))

Target "CreateGensrcDir" createGensrcDirs

Target "GenerateApi" generateApi 
Target "Default" id
Target "AppVeyor" id

Target "Pack" (fun _ ->
    IO.Directory.CreateDirectory "NuGet" |> ignore
    NuGetPack (fun p -> { p with Version = version; WorkingDir = __SOURCE_DIRECTORY__ }) "./RabbitMQ.Client.nuspec")

"Clean"
    ==> "CreateGensrcDir"
    ==> "BuildApigen"
    ==> "GenerateApi"
    ==> "UpdateAssemblyInfos"
    ==> "BuildClient"
    ==> "Default"

"BuildClient"
    ==> "Test"

"BuildClient"
    ==> "AppVeyorTest"
    ==> "AppVeyor"

RunTargetOrDefault "Default"