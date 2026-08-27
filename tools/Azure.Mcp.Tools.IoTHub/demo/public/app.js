const commandDefinitions = [
  {
    id: "endpoint-health",
    label: "endpoint-health",
    purpose: "Fast verdict across every configured routing destination.",
    depth: "Fast path",
    calls: ["hub-config", "endpoint-health", "routing-deliveries", "routing-latency", "target-existence"],
    consumption: "5 evidence sources · hub-wide metrics + endpoint existence",
    explanation: "One configuration read, one paged health read, two hub-wide Azure Monitor queries, and ARM existence checks for configured destinations.",
    input: {
      subscription: "string (configured by launcher)",
      resourceGroup: "string (configured by launcher)",
      hubName: "string (configured by launcher)",
      endpointName: "string?",
      lookback: "ISO 8601 duration or hours?",
      startTime: "ISO 8601 datetime?",
      endTime: "ISO 8601 datetime?",
    },
    output: {
      status: 200,
      message: "Success",
      results: {
        endpoints: [
          {
            name: "endpoint-name",
            endpointType: "EventHub",
            endpointHealthStatus: "healthy",
          },
        ],
      },
      duration: 1234,
    },
  },
  {
    id: "endpoint-latency",
    label: "endpoint-latency",
    purpose: "Delivery latency, threshold, peak, and time-bucket trend.",
    depth: "Trend",
    calls: ["hub-config", "endpoint-health", "routing-deliveries", "routing-latency", "target-existence"],
    consumption: "5 evidence sources · same reads as health, richer latency projection",
    explanation: "Consumes the same five sources as endpoint-health; the difference is output detail and interval-aware latency projection, not additional destination reads.",
    input: {
      subscription: "string (configured by launcher)",
      resourceGroup: "string (configured by launcher)",
      hubName: "string (configured by launcher)",
      endpointName: "string?",
      lookback: "ISO 8601 duration or hours?",
      startTime: "ISO 8601 datetime?",
      endTime: "ISO 8601 datetime?",
      interval: "PT1M | PT5M | PT15M | PT30M | PT1H | PT6H | PT12H | P1D",
    },
    output: {
      status: 200,
      message: "Success",
      results: {
        endpoints: [
          {
            name: "endpoint-name",
            endpointType: "StorageContainer",
            endpointHealthStatus: "healthy",
            routingDeliveryLatencyMsAvg: 65960,
            routingDeliveryLatencyMsPeak: 65960,
            latencyThresholdMs: 300000,
            sendToSuccessLatencyMs: 0,
            latencyTrend: [
              {
                timestamp: "2026-08-25T06:59:00+00:00",
                latencyMsAvg: 65960,
              },
            ],
          },
        ],
      },
      duration: 1234,
    },
  },
  {
    id: "endpoint-diagnose",
    label: "endpoint-diagnose",
    purpose: "Root-cause correlation with target metrics and configuration.",
    depth: "Deep dive",
    calls: [
      "hub-config",
      "endpoint-health",
      "routing-deliveries",
      "routing-latency",
      "target-existence",
      "eventhub-metrics",
      "servicebus-metrics",
      "storage-metrics",
      "cosmos-metrics",
      "storage-config",
    ],
    consumption: "10 evidence sources · hub evidence + destination deep dive",
    explanation: "Consumes all five health sources, then adds four destination-specific Azure Monitor queries and Storage network configuration for root-cause correlation.",
    input: {
      subscription: "string (configured by launcher)",
      resourceGroup: "string (configured by launcher)",
      hubName: "string (configured by launcher)",
      endpointName: "string?",
      lookback: "ISO 8601 duration or hours?",
      startTime: "ISO 8601 datetime?",
      endTime: "ISO 8601 datetime?",
      interval: "PT1M | PT5M | PT15M | PT30M | PT1H | PT6H | PT12H | P1D",
    },
    output: {
      status: 200,
      message: "Success",
      results: {
        endpoints: [
          {
            name: "storage-nonet-endpoint",
            endpointType: "StorageContainer",
            endpointResourceName: "storage-account",
            subscriptionId: "00000000-0000-0000-0000-000000000000",
            resourceGroup: "resource-group",
            endpointUri: "https://storage-account.blob.core.windows.net/",
            containerName: "telemetry",
            authenticationType: "identityBased",
            batchFrequencyInSeconds: 60,
            health: {
              endpointHealthStatus: "degraded",
              latencyThresholdMs: 300000,
              routedDeliveryFailures: 50,
              impactDetails: {
                confidenceScore: 0.9,
                likelyFaultDomain: "TargetNetwork",
                likelyFaultDetail: "Evidence-based explanation.",
              },
            },
            targetResourceSignals: {
              successfulRequests: 27,
              userErrors: 18,
              errorBreakdown: {
                AuthorizationError: 18,
              },
            },
            targetConfigurationSignals: {
              publicNetworkAccess: "Disabled",
              networkDefaultAction: "Deny",
              networkBypass: "None",
              warnings: ["Public network access is disabled."],
            },
            exploration: {
              targetResourceId: "/subscriptions/.../storageAccounts/storage-account",
              metricsQueried: ["Transactions", "RoutingDeliveries"],
              drillDownCommands: ["azmcp monitor metrics query ..."],
            },
          },
        ],
      },
      duration: 1234,
    },
  },
];

const environmentEndpoints = [
  { id: "eh-endpoint", type: "Event Hub", selector: "eventhub", group: "eventhub" },
  { id: "remote-eh-endpoint", type: "Remote Event Hub", selector: "remote", group: "eventhub" },
  { id: "sbqueue-endpoint", type: "Service Bus queue", selector: "sbqueue", group: "servicebus" },
  { id: "sbtopic-endpoint", type: "Service Bus topic", selector: "sbtopic", group: "servicebus" },
  { id: "storage-endpoint", type: "Blob container", selector: "storage", group: "storage" },
  { id: "cosmos-endpoint", type: "Cosmos DB container", selector: "cosmos", group: "cosmos" },
  { id: "storage-noauth-endpoint", type: "Blob container", selector: "noauth", group: "storage" },
  { id: "storage-nonet-endpoint", type: "Blob container", selector: "nonet", group: "storage" },
  { id: "sbqueue-missing-ns-endpoint", type: "Service Bus queue", selector: "missingresource", group: "servicebus" },
  { id: "sbqueue-missing-queue-endpoint", type: "Service Bus queue", selector: "missingsubresource", group: "servicebus" },
];

const testScenarios = [
  {
    id: "standard",
    label: "Standard traffic",
    action: "50 messages per target · parallel 4",
    endpoints: environmentEndpoints.map((endpoint) => endpoint.id),
    state: "healthy",
    expected: "endpoint-health: healthy; endpoint-latency: normal service latency; diagnose: no fault domain.",
  },
  {
    id: "burst",
    label: "Flood routing targets",
    action: "8,000 messages per target · parallel 32",
    endpoints: ["eh-endpoint", "remote-eh-endpoint", "sbqueue-endpoint", "sbtopic-endpoint", "storage-endpoint", "cosmos-endpoint"],
    state: "warning",
    expected: "RoutingDeliveries failures and latency can rise. endpoint-diagnose correlates throttling, server errors, and destination pressure.",
  },
  {
    id: "noauth",
    label: "Remove Storage role",
    action: "Delete Blob Data Contributor assignment",
    endpoints: ["storage-noauth-endpoint"],
    state: "warning",
    expected: "degraded · TargetAuthorization · Storage AuthorizationError evidence.",
  },
  {
    id: "nonet",
    label: "Block Storage network",
    action: "Public access Disabled · default Deny",
    endpoints: ["storage-nonet-endpoint"],
    state: "warning",
    expected: "degraded · TargetNetwork · firewall warning and failed deliveries.",
  },
  {
    id: "missing-namespace",
    label: "Delete Service Bus namespace",
    action: "Remove dedicated namespace",
    endpoints: ["sbqueue-missing-ns-endpoint"],
    state: "unavailable",
    expected: "unavailable · TargetUnavailable · namespace not found.",
  },
  {
    id: "missing-queue",
    label: "Delete Service Bus queue",
    action: "Remove break-queue; retain namespace",
    endpoints: ["sbqueue-missing-queue-endpoint"],
    state: "unavailable",
    expected: "unavailable · TargetUnavailable · queue not found.",
  },
];

const state = {
  config: null,
  selectedCommand: null,
};

const elements = {
  navItems: [...document.querySelectorAll(".nav-item")],
  pages: [...document.querySelectorAll(".page")],
  tableBody: document.querySelector("#command-table-body"),
  contractDetail: document.querySelector("#contract-detail"),
  architectureToolbar: document.querySelector("#architecture-toolbar"),
  consumptionSummary: document.querySelector("#consumption-summary"),
  endpointGrid: document.querySelector("#endpoint-grid"),
  scenarioList: document.querySelector("#scenario-list"),
  scenarioDetail: document.querySelector("#scenario-detail"),
  environmentActions: [...document.querySelectorAll(".environment-action")],
  actionStatus: document.querySelector("#action-status"),
  actionStatusTitle: document.querySelector("#action-status-title"),
  actionStatusState: document.querySelector("#action-status-state"),
  actionOutput: document.querySelector("#action-output"),
  toggleActionOutput: document.querySelector("#toggle-action-output"),
  launchVscodeAgent: document.querySelector("#launch-vscode-agent"),
  agentLaunchStatus: document.querySelector("#agent-launch-status"),
  connectionChip: document.querySelector("#connection-chip"),
  connectionLabel: document.querySelector("#connection-label"),
  footerTarget: document.querySelector("#footer-target"),
};

initialize();

async function initialize() {
  renderCommandTable();
  renderArchitectureToolbar();
  renderEnvironment();
  bindNavigation();
  bindVscodeLaunch();
  bindDemoActions();
  selectCommand(commandDefinitions[0]);
  renderCallEdges();
  requestAnimationFrame(layoutCallEdges);
  window.addEventListener("resize", layoutCallEdges);
  await loadConfig();
  await refreshActionStatus();
}

function bindNavigation() {
  for (const item of elements.navItems) {
    item.addEventListener("click", () => showPage(item.dataset.page));
  }
}

function showPage(pageId) {
  for (const item of elements.navItems) {
    item.classList.toggle("active", item.dataset.page === pageId);
  }
  for (const page of elements.pages) {
    page.classList.toggle("active", page.id === `page-${pageId}`);
  }
  if (pageId === "architecture") {
    requestAnimationFrame(layoutCallEdges);
  }
}

function renderCommandTable() {
  for (const command of commandDefinitions) {
    const row = document.createElement("tr");
    row.dataset.command = command.id;
    row.tabIndex = 0;
    row.innerHTML = `
      <td><span class="command-name">${escapeHtml(command.label)}</span></td>
      <td><span class="command-purpose">${escapeHtml(command.purpose)}</span></td>
      <td><span class="depth-badge">${escapeHtml(command.depth)}</span></td>
      <td><span class="row-arrow">›</span></td>
    `;
    row.addEventListener("click", () => selectCommand(command));
    row.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        selectCommand(command);
      }
    });
    elements.tableBody.append(row);
  }
}

function selectCommand(command) {
  state.selectedCommand = command;
  for (const row of elements.tableBody.querySelectorAll("tr")) {
    row.classList.toggle("selected", row.dataset.command === command.id);
  }

  elements.contractDetail.innerHTML = `
    <div class="detail-content">
      <div class="detail-header">
        <p class="eyebrow">${escapeHtml(command.depth)}</p>
        <h3>iothub routing ${escapeHtml(command.label)}</h3>
        <p>${escapeHtml(command.purpose)}</p>
      </div>
      ${renderCodeSection("MCP input", command.input)}
      ${renderCodeSection("JSON output", command.output)}
      <button class="send-vscode">
        Open ${escapeHtml(command.label)} in VS Code Agent
      </button>
    </div>
  `;

  elements.contractDetail.querySelector(".send-vscode").addEventListener("click", () => {
    launchVscodeAgent(promptForCommand(command.id));
  });
  for (const button of elements.contractDetail.querySelectorAll(".copy-button")) {
    button.addEventListener("click", async () => {
      const text = button.closest(".code-section").querySelector("pre").textContent;
      await navigator.clipboard.writeText(text);
      button.textContent = "Copied";
      setTimeout(() => { button.textContent = "Copy"; }, 1200);
    });
  }
}

function renderCodeSection(title, value) {
  return `
    <div class="code-section">
      <div class="code-section-header">
        <h4>${escapeHtml(title)}</h4>
        <button class="copy-button">Copy</button>
      </div>
      <pre>${escapeHtml(JSON.stringify(value, null, 2))}</pre>
    </div>
  `;
}

function renderArchitectureToolbar() {
  for (const command of commandDefinitions) {
    const button = document.createElement("button");
    button.className = "architecture-command";
    button.innerHTML = `
      <strong>${escapeHtml(command.label)}</strong>
      <small>${command.calls.length} sources</small>
    `;
    button.addEventListener("mouseenter", () => activateCallFlow(command));
    button.addEventListener("focus", () => activateCallFlow(command));
    button.addEventListener("mouseleave", clearCallFlow);
    button.addEventListener("blur", clearCallFlow);
    elements.architectureToolbar.append(button);
  }
}

function renderCallEdges() {
  const svg = document.querySelector(".call-lines");
  const nodes = [...document.querySelectorAll("[data-call-node]")];
  svg.replaceChildren(
    ...nodes.map((node) => {
      const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
      path.classList.add("call-edge");
      path.dataset.call = node.dataset.callNode;
      return path;
    }),
  );
}

function layoutCallEdges() {
  const map = document.querySelector("#call-map");
  const svg = map?.querySelector(".call-lines");
  const tool = map?.querySelector('[data-node="tool"]');
  if (!map || !svg || !tool || map.offsetParent === null) {
    return;
  }

  const mapRect = map.getBoundingClientRect();
  const toolRect = tool.getBoundingClientRect();
  const startX = toolRect.right - mapRect.left;
  const startY = toolRect.top + toolRect.height / 2 - mapRect.top;
  svg.setAttribute("viewBox", `0 0 ${mapRect.width} ${mapRect.height}`);

  for (const path of svg.querySelectorAll(".call-edge")) {
    const target = map.querySelector(`[data-call-node="${path.dataset.call}"]`);
    if (!target) {
      continue;
    }
    const targetRect = target.getBoundingClientRect();
    const endX = targetRect.left - mapRect.left;
    const endY = targetRect.top + targetRect.height / 2 - mapRect.top;
    const controlOffset = Math.max(70, (endX - startX) * 0.45);
    path.setAttribute(
      "d",
      `M ${startX} ${startY} C ${startX + controlOffset} ${startY}, ${endX - controlOffset} ${endY}, ${endX} ${endY}`,
    );
  }
}

function activateCallFlow(command) {
  layoutCallEdges();
  for (const button of elements.architectureToolbar.children) {
    button.classList.toggle("active", button.querySelector("strong").textContent === command.label);
  }
  for (const edge of document.querySelectorAll(".call-edge")) {
    edge.classList.toggle("active", command.calls.includes(edge.dataset.call));
  }
  for (const node of document.querySelectorAll("[data-call-node]")) {
    node.classList.toggle("active", command.calls.includes(node.dataset.callNode));
  }
  elements.consumptionSummary.innerHTML = `
    <span class="summary-label">Evidence footprint</span>
    <strong>${escapeHtml(command.consumption)}</strong>
    <small>${escapeHtml(command.explanation)}</small>
  `;
}

function clearCallFlow() {
  for (const element of document.querySelectorAll(".architecture-command, .call-edge, [data-call-node]")) {
    element.classList.remove("active");
  }
  elements.consumptionSummary.innerHTML = `
    <span class="summary-label">Evidence footprint</span>
    <strong>Select a command</strong>
    <small>Highlighted cards are data sources consumed by that command.</small>
  `;
}

function renderEnvironment() {
  for (const endpoint of environmentEndpoints) {
    const card = document.createElement("div");
    card.className = "endpoint-card";
    card.dataset.endpoint = endpoint.id;
    card.dataset.group = endpoint.group;
    card.innerHTML = `
      <span>${escapeHtml(endpoint.type)}</span>
      <strong>${escapeHtml(endpoint.id)}</strong>
      <small>target = '${escapeHtml(endpoint.selector)}'</small>
      <em class="endpoint-state">healthy baseline</em>
    `;
    elements.endpointGrid.append(card);
  }

  for (const scenario of testScenarios) {
    const button = document.createElement("button");
    button.className = "scenario-button";
    button.dataset.scenario = scenario.id;
    button.innerHTML = `
      <strong>${escapeHtml(scenario.label)}</strong>
      <small>${escapeHtml(scenario.action)}</small>
    `;
    button.addEventListener("mouseenter", () => activateScenario(scenario));
    button.addEventListener("focus", () => activateScenario(scenario));
    button.addEventListener("mouseleave", clearScenario);
    button.addEventListener("blur", clearScenario);
    elements.scenarioList.append(button);
  }
}

function activateScenario(scenario) {
  for (const card of elements.endpointGrid.children) {
    const selected = scenario.endpoints.includes(card.dataset.endpoint);
    card.classList.toggle("scenario-active", selected);
    card.classList.toggle("scenario-muted", !selected);
    card.dataset.state = selected ? scenario.state : "";
    card.querySelector(".endpoint-state").textContent = selected
      ? scenario.state === "warning" ? "expected degraded" : scenario.state === "unavailable" ? "expected unavailable" : "expected healthy"
      : "not affected";
  }
  for (const button of elements.scenarioList.children) {
    button.classList.toggle("active", button.dataset.scenario === scenario.id);
  }
  elements.scenarioDetail.innerHTML = `
    <span>EXPECTED MCP OBSERVATION</span>
    <strong>${escapeHtml(scenario.label)}</strong>
    <p>${escapeHtml(scenario.expected)}</p>
  `;
}

function clearScenario() {
  for (const card of elements.endpointGrid.children) {
    card.classList.remove("scenario-active", "scenario-muted");
    card.dataset.state = "";
    card.querySelector(".endpoint-state").textContent = "healthy baseline";
  }
  for (const button of elements.scenarioList.children) {
    button.classList.remove("active");
  }
  elements.scenarioDetail.innerHTML = `
    <strong>Choose a scenario</strong>
    <p>Related endpoints and expected MCP signals will highlight here.</p>
  `;
}

function bindVscodeLaunch() {
  elements.launchVscodeAgent.addEventListener("click", () => {
    launchVscodeAgent("Show me the health of all routing endpoints for the past 6 hours.");
  });
}

function bindDemoActions() {
  for (const button of elements.environmentActions) {
    button.addEventListener("click", async () => {
      const action = button.dataset.action;
      const required = button.dataset.confirmation;
      const confirmation = window.prompt(
        `${button.querySelector("strong").textContent}\n\nType ${required} to continue.`,
      );
      if (confirmation === null) {
        return;
      }
      if (confirmation !== required) {
        showActionMessage(button.querySelector("strong").textContent, "confirmation rejected", `Expected ${required}.`);
        return;
      }
      await startDemoAction(action, confirmation);
    });
  }
  elements.toggleActionOutput.addEventListener("click", () => {
    const hidden = elements.actionOutput.hidden;
    elements.actionOutput.hidden = !hidden;
    elements.toggleActionOutput.textContent = hidden ? "Hide output" : "Show output";
  });
}

async function startDemoAction(action, confirmation) {
  setActionButtonsDisabled(true);
  showActionMessage(actionTitle(action), "starting", "");
  try {
    const response = await fetch("/api/action", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ action, confirmation }),
    });
    const result = await response.json();
    if (!response.ok) {
      throw new Error(result.error ?? "Unable to start demo action.");
    }
    renderActionStatus(result);
    scheduleActionPoll();
  } catch (error) {
    showActionMessage(actionTitle(action), "failed", error.message);
    setActionButtonsDisabled(false);
  }
}

async function refreshActionStatus() {
  try {
    const response = await fetch("/api/action/status");
    const status = await response.json();
    renderActionStatus(status);
    if (status.running) {
      scheduleActionPoll();
    }
  } catch {
    if (elements.environmentActions.some((button) => button.disabled)) {
      scheduleActionPoll();
    }
  }
}

function scheduleActionPoll() {
  window.setTimeout(refreshActionStatus, 1000);
}

function renderActionStatus(status) {
  if (!status.action) {
    elements.actionStatus.hidden = true;
    setActionButtonsDisabled(false);
    return;
  }

  const action = status.action;
  elements.actionStatus.hidden = false;
  elements.actionStatusTitle.textContent = actionTitle(action.id);
  elements.actionStatusState.textContent = status.running
    ? `running · ${elapsed(action.startedAt)}`
    : `${action.status} · exit ${action.exitCode ?? "n/a"}`;
  elements.actionOutput.textContent = [action.output, action.error]
    .filter((value) => value?.trim())
    .join("\n")
    .trim() || "Waiting for output…";
  setActionButtonsDisabled(status.running);
}

function showActionMessage(title, state, output) {
  elements.actionStatus.hidden = false;
  elements.actionStatusTitle.textContent = title;
  elements.actionStatusState.textContent = state;
  elements.actionOutput.textContent = output;
}

function setActionButtonsDisabled(disabled) {
  for (const button of elements.environmentActions) {
    button.disabled = disabled;
  }
}

function actionTitle(action) {
  return {
    provision: "Provision resources",
    break: "Break test resources",
    standard: "Send regular traffic",
    burst: "Send throttle traffic",
  }[action] ?? action;
}

function elapsed(startedAt) {
  const seconds = Math.max(0, Math.floor((Date.now() - Date.parse(startedAt)) / 1000));
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

async function launchVscodeAgent(prompt) {
  elements.launchVscodeAgent.disabled = true;
  elements.launchVscodeAgent.textContent = "Opening…";
  try {
    const response = await fetch("/api/vscode", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ prompt }),
    });
    const result = await response.json();
    if (!response.ok) {
      throw new Error(result.error ?? "Unable to launch VS Code.");
    }
    elements.agentLaunchStatus.textContent = `Opened ${result.agent} in VS Code · tools: local-mcp/*`;
  } catch (error) {
    elements.agentLaunchStatus.textContent = error.message;
  } finally {
    elements.launchVscodeAgent.disabled = false;
    elements.launchVscodeAgent.textContent = "Open VS Code Agent";
  }
}

async function loadConfig() {
  try {
    const response = await fetch("/api/config");
    state.config = await response.json();
    const target = `${state.config.subscription} · ${state.config.resourceGroup} · ${state.config.hubName}`;
    const ready = state.config.executableAvailable && state.config.vscodeAvailable;
    elements.connectionChip.classList.add(ready ? "online" : "offline");
    elements.connectionLabel.textContent = ready ? "VS Code + local MCP ready" : "Setup required";
    elements.footerTarget.textContent = target;
  } catch (error) {
    elements.connectionChip.classList.add("offline");
    elements.connectionLabel.textContent = "Local server unavailable";
  }
}

function promptForCommand(commandId) {
  switch (commandId) {
    case "endpoint-health":
      return "Show me the health of all routing endpoints for the past 6 hours";
    case "endpoint-latency":
      return "Compare routing endpoint latency for the past 6 hours using 15-minute buckets";
    case "endpoint-diagnose":
      return "Diagnose all routing endpoint issues for the past 6 hours and explain the likely causes";
    default:
      return "Show me the IoT Hub routing status";
  }
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}
