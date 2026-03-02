## Phoenix's Api Wrappers
This repository holds automatically generated (by kiota) C# wrappers for Phoenix api's.

If you'd like to work in a different language, you can find the OpenAPI documents in this repository.
`src/Phoenix.ApiWrapper/OpenApiDocuments`

## Installation
Install the package through nuget:  
`dotnet add package Newtonsoft.Json --version 13.0.4`

Or add the PackageReference to your .csproj file:  
`<PackageReference Include="Newtonsoft.Json" Version="13.0.4" />`

## Getting started
You can look at our Example project in this repository to see how to use it.

No docs available atm.

## (Re-)Generating the wrapper
Easiest option is to enable generation on build inside of `Phoenix.ApiWrapper.csproj``:

```xml
<PropertyGroup>
    <GenerateKiotaOnBuild>false</GenerateKiotaOnBuild>
</PropertyGroup>
```

Simply set it to true.  
You can also run the command manually, once you've installed kiota as a tool:

`dotnet tool run kiota generate --openapi "src/Phoenix.ApiWrapper/OpenApiDocuments/phoenix_api.json" --language csharp --output "src\Phoenix.ApiWrapper\Generated\PhoenixNetwork" --namespace-name "Phoenix.Api"`
