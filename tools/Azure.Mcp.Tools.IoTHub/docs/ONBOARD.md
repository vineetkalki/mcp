# IoT Hub MCP Tool: Contributor Onboarding

Welcome! This guide is for **Azure IoT Hub team members** contributing to the IoT Hub toolset in the Azure
MCP Server (`azmcp`). It walks through account/access setup, getting the code, the IoT Hub tool layout, and
the local development and testing workflows in depth.

The IoT Hub toolset lives at `tools/Azure.Mcp.Tools.IoTHub`. Its commands are exposed to AI agents as MCP
tools (e.g. `iothub_hub_get`, `iothub_routing_endpoint-health`) and can also be run directly from the CLI.

---

<details open>
<summary><strong>Account Setup and Access</strong></summary>

## Link your work account to your GitHub account
1. Go to the Microsoft GitHub onboarding portal: https://aka.ms/opensource/portal
2. Sign in with your Microsoft work account.
3. Sign in with the GitHub account you want to use.
4. Authorize the connection between the two accounts.
5. Accept the GitHub organization invitation.

## After linking accounts
1. You should receive an invitation from the Microsoft GitHub organization.
2. Check:
   - Your GitHub email inbox
   - GitHub notifications
   - https://github.com/settings/organizations
3. Accept the invitation.

## Join the Azure organization
1. Request access at https://repos.opensource.microsoft.com/orgs/Azure

</details>

<details open>
<summary><strong>Getting started with the Azure MCP Server</strong></summary>

Read:
- [MCP Getting Started (eng.ms)](https://eng.ms/docs/products/azure-developer-experience/mcp/mcp-getting-started)

Clone:
- `git clone https://github.com/microsoft/mcp.git`
- Server source: https://github.com/microsoft/mcp/tree/main/servers/Azure.Mcp.Server

Run the released MCP server:
- [Get started with the Azure MCP Server in VS Code](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/get-started/tools/visual-studio-code?tabs=one-click)

Run a local development MCP server:
- [Azure MCP Server local development (Azure.Mcp.Server)](https://github.com/microsoft/mcp/tree/main/servers/Azure.Mcp.Server)

</details>

<details open>
<summary><strong>Local Development</strong></summary>

The IoT Hub toolset builds into the shared `azmcp` server binary. There are several ways to run and iterate
on your changes locally, from a fast one-shot CLI to a full MCP server that VS Code Copilot drives.

<details open>
<summary><strong>Prerequisites</strong></summary>

- **.NET SDK** matching [`global.json`](../../../global.json) (currently .NET 10).
- **PowerShell 7+**, **Node.js LTS** (`node`/`npm` on PATH), and **Git**.
- **Azure CLI** (`az login`) and/or **Azure PowerShell** (`Connect-AzAccount`) for anything that touches Azure.
- **Azure Bicep** (only needed for live-test infrastructure).
- **VS Code** with the **GitHub Copilot** + **Copilot Chat** extensions.

</details>

<details open>
<summary><strong>Build</strong></summary>

```powershell
# Fast inner loop: build just the IoT Hub toolset
dotnet build tools/Azure.Mcp.Tools.IoTHub/src

# Build the server that hosts the toolset (needed before running the MCP server / CLI)
dotnet build servers/Azure.Mcp.Server/src

# Full verification (build + npx packaging checks) before opening a PR
./eng/scripts/Build-Local.ps1 -VerifyNpx
```

The IoT Hub tool lives at `tools/Azure.Mcp.Tools.IoTHub`:

| Path | Purpose |
| --- | --- |
| `src/Commands/` | Command implementations (`{Resource}{Operation}Command`) grouped by resource |
| `src/Options/` | Flat `[Option]` POCOs bound to command inputs |
| `src/Services/` | `IIoTHubService` + `IoTHubService` (ARM / Azure Monitor calls) |
| `src/Models/` | Response models registered in `IoTHubJsonContext` |
| `src/IoTHubSetup.cs` | Registers commands + the command-group tree with the server |
| `tests/Azure.Mcp.Tools.IoTHub.UnitTests/` | Unit tests (no Azure required) |
| `tests/Azure.Mcp.Tools.IoTHub.LiveTests/` | Live tests (real Azure resources) |
| `tests/test-resources.bicep` | Live-test infrastructure |

</details>

<details open>
<summary><strong>Development methods</strong></summary>

### Method 1: One-shot CLI (fastest inner loop)

The server binary is also a CLI: run any command once and it prints JSON and exits, with no MCP server, VS
Code, or agent involved. Best for quickly checking a command's output while iterating.

```powershell
$azmcp = ".\servers\Azure.Mcp.Server\src\bin\Debug\net10.0\azmcp.exe"

& $azmcp iothub hub get `
  --subscription <sub-id-or-name> --resource-group <rg> --hub-name <hub>

& $azmcp iothub routing endpoint-health `
  --subscription <sub-id-or-name> --resource-group <rg> --hub-name <hub> --lookback PT2H

& $azmcp iothub routing endpoint-latency `
  --subscription <sub-id-or-name> --resource-group <rg> --hub-name <hub> --lookback PT2H

& $azmcp iothub routing endpoint-diagnose `
  --subscription <sub-id-or-name> --resource-group <rg> --hub-name <hub> `
  --start-time 2026-08-01T00:00:00Z --end-time 2026-08-02T00:00:00Z --interval PT15M
```

Use `--lookback` for a relative window, or supply `--start-time` and `--end-time` together for an
absolute window. Absolute times take precedence when `--lookback` is also present. Windows are limited
to 30 days.

`dotnet servers/Azure.Mcp.Server/src/bin/Debug/net10.0/azmcp.dll iothub hub get ...` is the cross-platform
equivalent. Rebuild the tool/server after code changes for the CLI to pick them up.

### Method 2: Local MCP server in VS Code (agent-driven)

This is how the tool is actually consumed: VS Code starts the server over stdio and Copilot calls the tools.
Create `.vscode/mcp.json`:

```json
{
  "servers": {
    "local-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "servers/Azure.Mcp.Server/src/bin/Debug/net10.0/azmcp.dll",
        "server",
        "start",
        "--namespace",
        "iothub"
      ]
    }
  },
  "inputs": []
}
```

Start it from the MCP view (or the **Start** code-lens above the server in `mcp.json`), then use it from
Copilot Chat in **Agent** mode. You don't type CLI flags; you ask in natural language and Copilot invokes
the tool. The IoT Hub commands surface as tools named `iothub_<group>_<command>`, e.g.:

- `iothub_hub_get`
- `iothub_routing_endpoint-health`
- `iothub_routing_endpoint-latency`
- `iothub_routing_endpoint-diagnose`

Example prompt:

> Diagnose the routing endpoints for IoT Hub `iothrottlehubs3576f` in resource group `mcp-throttle` over the
> last 2 hours.

Tool arguments use the kebab-case option names (`hub-name`, `resource-group`, `subscription`,
`lookback`, `endpoint-name`, …).

**Server modes** (change the `args`):

| Args | Effect |
| --- | --- |
| `server start` | Expose **all** toolsets |
| `server start --namespace iothub` | Only the IoT Hub tools (recommended while iterating) |
| `server start --namespace iothub --namespace storage` | Multiple specific namespaces |
| `server start --mode namespace` | Group tools under one proxy tool per namespace (helps with VS Code's 128-tool limit) |
| `server start --mode single` | Expose a single `azure` tool that routes internally |

### Method 3: `dotnet run` / debugger (F5)

Run the server from source (rebuilds automatically):

```powershell
dotnet run --project servers/Azure.Mcp.Server/src --launch-profile local
```

To **debug**, open the repo in VS Code / Visual Studio and launch the `local` profile
(`servers/Azure.Mcp.Server/src/Properties/launchSettings.json`), then set breakpoints in your command or
service and step through as the server handles tool calls. The `debug-remotemcp` profile starts the server
over HTTP for remote / on-behalf-of scenarios.

### Rebuild and restart workflow

The running server holds the built DLL, so **after rebuilding you must restart it** to pick up changes:

- **VS Code MCP server:** use the **Restart** action on the server in the MCP view (or toggle it off/on).
- **CLI / stray processes:** stop any running instances first, e.g.:

```powershell
Stop-Process -Name azmcp -Force -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  Where-Object { $_.CommandLine -like '*azmcp.dll server*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

</details>

<details open>
<summary><strong>Testing</strong></summary>

```powershell
# Unit tests (no Azure), the everyday inner loop
dotnet test tools/Azure.Mcp.Tools.IoTHub/tests/Azure.Mcp.Tools.IoTHub.UnitTests

# Or via the repo script
./eng/scripts/Test-Code.ps1 -Paths IoTHub

# Run a single test class
dotnet test tools/Azure.Mcp.Tools.IoTHub/tests/Azure.Mcp.Tools.IoTHub.UnitTests `
  --filter "FullyQualifiedName~RoutingEndpointHealthGetCommandTests"
```

**Live tests** exercise commands against real Azure resources and must be recorded for playback (see
[`docs/recorded-tests.md`](../../../docs/recorded-tests.md)). To provision the test infra and run them:

```powershell
Connect-AzAccount    # and: az login

# Deploy tools/Azure.Mcp.Tools.IoTHub/tests/test-resources.bicep
eng/common/TestResources/New-TestResources.ps1 -TestResourcesDirectory tools/Azure.Mcp.Tools.IoTHub

./eng/scripts/Test-Code.ps1 -TestType Live -Paths IoTHub
```

</details>

<details open>
<summary><strong>Adding or changing a command</strong></summary>

1. Follow the guided skill: [`.github/skills/add-azure-mcp-tools/SKILL.md`](../../../.github/skills/add-azure-mcp-tools/SKILL.md).
2. Follow repo conventions in [`AGENTS.md`](../../../AGENTS.md) and the two-generic option pattern in
   [`docs/option-conversion.md`](../../../docs/option-conversion.md): primary constructors, `sealed` command
   classes, one class per file, `System.Text.Json`, and AOT-safe models registered in `IoTHubJsonContext`.
3. Register the command in `src/IoTHubSetup.cs`.
4. Add unit tests (and live tests + Bicep for anything hitting Azure).
5. Update the command docs and e2e test prompts, and add a changelog entry under
   `servers/Azure.Mcp.Server/changelog-entries/`. See the PR checklist in [`AGENTS.md`](../../../AGENTS.md)
   for the exact files.
6. Validate the tool description with the ToolDescriptionEvaluator (target score ≥ 0.4) and run the spelling
   check `.\eng\common\spelling\Invoke-Cspell.ps1`.

</details>

<details open>
<summary><strong>Troubleshooting</strong></summary>

- **Changes not showing up in Copilot:** you rebuilt but didn't restart the MCP server, so restart it (see above).
- **Tool not listed:** confirm the namespace filter includes `iothub` and that the command is registered in
  `IoTHubSetup.cs`; check the server started without errors in the MCP output.
- **`az login` / auth errors:** the tools use your local Azure credentials, so sign in with `az login` (and
  `Connect-AzAccount` for live tests) and confirm the target subscription.
- **Build lock on `azmcp.dll`:** stop the running server / CLI processes first (see the rebuild and restart snippet).

</details>

</details>