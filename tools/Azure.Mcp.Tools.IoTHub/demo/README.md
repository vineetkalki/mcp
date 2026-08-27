# IoT Hub Routing Demo

This temporary demo presents the three IoT Hub routing commands and runs them against the local Azure
MCP CLI implementation.

## Start

Build the local server first:

```powershell
dotnet build servers\Azure.Mcp.Server\src\Azure.Mcp.Server.csproj
```

Then launch the demo:

```powershell
.\tools\Azure.Mcp.Tools.IoTHub\demo\Start-Demo.ps1
```

The launcher starts a local web server and opens the demo in a new browser window. Defaults target:

- Subscription: `iot-sub-1038`
- Resource group: `mcp-throttle`
- IoT Hub: `iothrottlehubs3576f`
- URL: `http://127.0.0.1:4173`

Override them when needed:

```powershell
.\tools\Azure.Mcp.Tools.IoTHub\demo\Start-Demo.ps1 `
  -Subscription <subscription> `
  -ResourceGroup <resource-group> `
  -HubName <hub-name> `
  -Port 4173 `
  -CodePath "C:\Program Files\Microsoft VS Code\Code.exe"
```

## Stop

```powershell
.\tools\Azure.Mcp.Tools.IoTHub\demo\Stop-Demo.ps1
```

## Demo pages

1. **Command contracts** — click command rows to inspect MCP input and JSON output shapes.
2. **Azure data flow** — hover command names to highlight the Azure APIs and data sources queried.
3. **Demo environment** — inspect the provisioned routing targets and hover break/flood scenarios.

The environment page also includes four live controls:

- **Provision resources** — runs `environment/setup.ps1 -SkipBreak`, which deploys
  `environment/azuredeploy.json` in a healthy state.
- **Break test resources** — runs `environment/break-endpoints.ps1`.
- **Send regular traffic** — runs `environment/send-messages.ps1 -Mode Standard -Targets all`.
- **Send throttle traffic** — runs `environment/send-messages.ps1 -Mode Burst -Targets all`.

Each control requires an explicit confirmation phrase, only one action can run at a time, and the page
shows live output. `Stop-Demo.ps1` refuses to stop the server while an action is active.

The **Open VS Code Agent** button launches a temporary custom agent in the current VS Code window. Its tool
list is restricted to `local-mcp/*`; subagents, shell, editing, and other tools are unavailable. The launcher
installs the temporary agent under `.github/agents`, and `Stop-Demo.ps1` removes it when its contents still
match the demo template.
