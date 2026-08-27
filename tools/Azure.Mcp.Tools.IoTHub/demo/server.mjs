import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { createReadStream, existsSync, statSync, unlinkSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { dirname, extname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const demoRoot = dirname(fileURLToPath(import.meta.url));
const publicRoot = join(demoRoot, "public");
const repoRoot = resolve(demoRoot, "..", "..", "..");
const executable = join(repoRoot, "servers", "Azure.Mcp.Server", "src", "bin", "Debug", "net10.0", "azmcp.exe");

const argumentsMap = parseArguments(process.argv.slice(2));
const instanceToken = argumentsMap.get("--instance-token") ?? randomUUID().replaceAll("-", "");
const config = {
  port: parsePort(argumentsMap.get("--port") ?? "4173"),
  subscription: argumentsMap.get("--subscription") ?? "iot-sub-1038",
  resourceGroup: argumentsMap.get("--resource-group") ?? "mcp-throttle",
  hubName: argumentsMap.get("--hub-name") ?? "iothrottlehubs3576f",
};
const configuredCodePath = argumentsMap.get("--code-path");
const codeExecutable = configuredCodePath && existsSync(configuredCodePath)
  ? configuredCodePath
  : null;
const configuredPwshPath = argumentsMap.get("--pwsh-path");
const pwshExecutable = configuredPwshPath && existsSync(configuredPwshPath)
  ? configuredPwshPath
  : null;
const actionScript = join(demoRoot, "Invoke-DemoAction.ps1");
const actionStatePath = join(demoRoot, ".demo-action.json");
const actionDefinitions = new Map([
  ["provision", { scriptAction: "Provision", confirmation: "PROVISION", timeoutMs: 30 * 60_000 }],
  ["break", { scriptAction: "Break", confirmation: "BREAK", timeoutMs: 10 * 60_000 }],
  ["standard", { scriptAction: "Standard", confirmation: "SEND", timeoutMs: 15 * 60_000 }],
  ["burst", { scriptAction: "Burst", confirmation: "THROTTLE", timeoutMs: 45 * 60_000 }],
]);
let activeAction = null;
let lastAction = null;

const mimeTypes = new Map([
  [".html", "text/html; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".svg", "image/svg+xml"],
]);

const server = createServer(async (request, response) => {
  setSecurityHeaders(response);

  if (request.method === "GET" && request.url === "/api/health") {
    return sendJson(response, 200, { status: "ready", instanceToken });
  }

  if (request.method === "GET" && request.url === "/api/config") {
    return sendJson(response, 200, {
      ...config,
      executable,
      executableAvailable: existsSync(executable),
      codeExecutable,
      vscodeAvailable: Boolean(codeExecutable),
      pwshAvailable: Boolean(pwshExecutable),
    });
  }

  if (request.method === "POST" && request.url === "/api/vscode") {
    try {
      const body = await readJson(request);
      const result = await launchVscodeAgent(body.prompt);
      return sendJson(response, 200, result);
    } catch (error) {
      return sendJson(response, error.statusCode ?? 400, {
        error: error.message,
      });
    }
  }

  if (request.method === "GET" && request.url === "/api/action/status") {
    return sendJson(response, 200, actionStatus());
  }

  if (request.method === "POST" && request.url === "/api/action") {
    try {
      const body = await readJson(request);
      const result = startDemoAction(body.action, body.confirmation);
      return sendJson(response, 202, result);
    } catch (error) {
      return sendJson(response, error.statusCode ?? 400, { error: error.message });
    }
  }

  if (request.method !== "GET") {
    return sendJson(response, 405, { error: "Method not allowed." });
  }

  return serveStatic(request.url, response);
});

server.listen(config.port, "127.0.0.1", () => {
  console.log(`IoT Hub demo listening at http://127.0.0.1:${config.port}`);
  console.log(`Using ${executable}`);
});

function parseArguments(args) {
  const result = new Map();
  for (let index = 0; index < args.length; index += 2) {
    const key = args[index];
    const value = args[index + 1];
    if (!key?.startsWith("--") || value === undefined) {
      throw new Error(`Invalid server argument: ${key ?? ""}`);
    }
    result.set(key, value);
  }
  return result;
}

function parsePort(value) {
  const port = Number.parseInt(value, 10);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    throw new Error("Port must be between 1024 and 65535.");
  }
  return port;
}

function launchVscodeAgent(rawPrompt) {
  if (typeof rawPrompt !== "string" || rawPrompt.trim().length === 0) {
    throw new Error("Enter a prompt.");
  }

  if (rawPrompt.length > 2000) {
    throw new Error("Prompt is too long.");
  }
  if (rawPrompt.trim().startsWith("-")) {
    throw new Error("Prompt cannot begin with an option marker.");
  }
  if (!codeExecutable) {
    throw new Error("Native VS Code executable is unavailable.");
  }

  const prompt = [
    `Use subscription '${config.subscription}', resource group '${config.resourceGroup}', and IoT Hub '${config.hubName}'.`,
    rawPrompt.trim(),
  ].join("\n");
  return new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(
      codeExecutable,
      ["chat", "--mode", "iothub-routing-demo-temp", "--reuse-window", "--maximize", prompt],
      {
        cwd: repoRoot,
        shell: false,
        windowsHide: false,
      },
    );
    let stderr = "";
    const timeout = setTimeout(() => {
      child.kill();
      rejectPromise(new Error("VS Code did not acknowledge the agent launch within 20 seconds."));
    }, 20_000);
    child.stderr.on("data", (data) => { stderr += data.toString(); });
    child.on("error", (error) => {
      clearTimeout(timeout);
      rejectPromise(error);
    });
    child.on("close", (exitCode) => {
      clearTimeout(timeout);
      if (exitCode !== 0) {
        rejectPromise(new Error(stderr.trim() || `VS Code exited with code ${exitCode}.`));
        return;
      }
      resolvePromise({
        launched: true,
        agent: "iothub-routing-demo-temp",
        prompt,
      });
    });
  });
}

function startDemoAction(actionId, confirmation) {
  if (activeAction) {
    const error = new Error(`Action '${activeAction.id}' is already running.`);
    error.statusCode = 409;
    throw error;
  }
  const definition = actionDefinitions.get(actionId);
  if (!definition) {
    throw new Error("Unknown demo action.");
  }
  if (confirmation !== definition.confirmation) {
    throw new Error(`Confirmation must exactly match '${definition.confirmation}'.`);
  }
  if (!pwshExecutable || !existsSync(actionScript)) {
    throw new Error("PowerShell 7 or the demo action runner is unavailable.");
  }

  const state = {
    id: actionId,
    status: "running",
    startedAt: new Date().toISOString(),
    completedAt: null,
    exitCode: null,
    output: "",
    error: "",
  };
  activeAction = state;
  const child = spawn(
    pwshExecutable,
    [
      "-NoLogo",
      "-NoProfile",
      "-NonInteractive",
      "-File",
      actionScript,
      "-Action",
      definition.scriptAction,
      "-Subscription",
      config.subscription,
      "-ResourceGroup",
      config.resourceGroup,
      "-HubName",
      config.hubName,
      "-InstanceToken",
      instanceToken,
    ],
    {
      cwd: repoRoot,
      shell: false,
      windowsHide: true,
    },
  );
  writeFileSync(
    actionStatePath,
    JSON.stringify({
      pid: child.pid,
      actionId,
      actionScript,
      instanceToken,
      serverPid: process.pid,
    }),
  );
  const timeout = setTimeout(() => {
    child.kill();
    state.status = "failed";
    state.error += `\nAction timed out after ${definition.timeoutMs / 60_000} minutes.`;
  }, definition.timeoutMs);
  child.stdout.on("data", (data) => {
    state.output = appendBounded(state.output, data.toString());
  });
  child.stderr.on("data", (data) => {
    state.error = appendBounded(state.error, data.toString());
  });
  child.on("error", (error) => {
    clearTimeout(timeout);
    state.status = "failed";
    state.error = appendBounded(state.error, error.message);
    state.completedAt = new Date().toISOString();
    lastAction = state;
    activeAction = null;
    clearActionState();
  });
  child.on("close", (exitCode) => {
    clearTimeout(timeout);
    state.exitCode = exitCode;
    state.status = state.status === "failed" || exitCode !== 0 ? "failed" : "completed";
    state.completedAt = new Date().toISOString();
    lastAction = state;
    activeAction = null;
    clearActionState();
  });

  return actionStatus();
}

function actionStatus() {
  return {
    running: Boolean(activeAction),
    action: activeAction ?? lastAction,
  };
}

function appendBounded(current, next) {
  const combined = current + next;
  return combined.length <= 250_000 ? combined : combined.slice(combined.length - 250_000);
}

function clearActionState() {
  try {
    unlinkSync(actionStatePath);
  } catch (error) {
    if (error.code !== "ENOENT") {
      console.error(`Unable to remove action state: ${error.message}`);
    }
  }
}

function serveStatic(url, response) {
  let requestPath;
  try {
    requestPath = decodeURIComponent((url ?? "/").split("?")[0]);
  } catch {
    return sendJson(response, 400, { error: "Malformed request path." });
  }

  const relativePath = requestPath === "/" ? "index.html" : requestPath.replace(/^\/+/, "");
  const filePath = resolve(publicRoot, relativePath);
  if (!filePath.startsWith(`${publicRoot}${sep}`) || !existsSync(filePath) || !statSync(filePath).isFile()) {
    return sendJson(response, 404, { error: "Not found." });
  }

  response.writeHead(200, {
    "Content-Type": mimeTypes.get(extname(filePath)) ?? "application/octet-stream",
    "Cache-Control": "no-store",
  });
  createReadStream(filePath).pipe(response);
}

function readJson(request) {
  return new Promise((resolvePromise, rejectPromise) => {
    let body = "";
    request.on("data", (chunk) => {
      body += chunk;
      if (body.length > 10_000) {
        request.destroy();
        rejectPromise(new Error("Request body is too large."));
      }
    });
    request.on("end", () => {
      try {
        resolvePromise(JSON.parse(body));
      } catch {
        rejectPromise(new Error("Request body must be valid JSON."));
      }
    });
    request.on("error", rejectPromise);
  });
}

function sendJson(response, statusCode, value) {
  response.writeHead(statusCode, { "Content-Type": "application/json; charset=utf-8" });
  response.end(JSON.stringify(value));
}

function setSecurityHeaders(response) {
  response.setHeader("Content-Security-Policy", "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'");
  response.setHeader("X-Content-Type-Options", "nosniff");
  response.setHeader("Referrer-Policy", "no-referrer");
}
