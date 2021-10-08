# PerfViewJS

PerfViewJS is a webviewer for ETL, NetPerf and NetTrace data.

## Usage

* On terminal: `dotnet run -p src/PerfViewJS.csproj 5000 .`
* Open another terminal and run:
* * `cd src/spa`
* * `npm run start`
* Browse to http://localhost:5000
* Place your nettrace, etl, netperf, or btl file in repositories root directory

## Debugging

* Press F5 in Visual Studio
* cd src/spa
* npm run start
* Browse to http://localhost:3000

In VSCode:
* Select the "Run & debug everything" launch task on Run & Debug menu
* This will launch port 5000 by default for the web server and port 3000 for SPA

## Todo

* Wrap PerfViewJS as a dotnet global tool
* Use Chromium Embedded Framework to make a client-side application

## Components

PerfViewJS is an ASP.NET Core application. React is used for rendering and GUI state.