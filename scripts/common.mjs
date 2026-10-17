import fs from 'node:fs';
import path from 'node:path';
import { spawn, execSync } from 'node:child_process';

/**
 * ANSI Color helpers for console output
 */
export const colors = {
  reset: '\x1b[0m',
  bold: '\x1b[1m',
  green: '\x1b[32m',
  yellow: '\x1b[33m',
  blue: '\x1b[34m',
  magenta: '\x1b[35m',
  cyan: '\x1b[36m',
  red: '\x1b[31m',
  gray: '\x1b[90m',
};

export function logStep(step, total, message) {
  console.log(`\n${colors.cyan}${colors.bold}[${step}/${total}]${colors.reset} ${colors.bold}${message}${colors.reset}`);
}

export function logSuccess(message) {
  console.log(`${colors.green}✔ ${message}${colors.reset}`);
}

export function logInfo(message) {
  console.log(`${colors.blue}ℹ ${message}${colors.reset}`);
}

export function logWarn(message) {
  console.log(`${colors.yellow}⚠ ${message}${colors.reset}`);
}

export function logError(message) {
  console.error(`${colors.red}✖ ${message}${colors.reset}`);
}

/**
 * Parse .env file without external dependencies
 */
export function loadEnv(rootDir) {
  const envPath = path.join(rootDir, '.env');
  const examplePath = path.join(rootDir, '.env.example');

  let envContent = '';
  if (fs.existsSync(envPath)) {
    envContent = fs.readFileSync(envPath, 'utf-8');
  } else if (fs.existsSync(examplePath)) {
    logWarn(`.env not found. Using default configurations from .env.example`);
    envContent = fs.readFileSync(examplePath, 'utf-8');
  }

  const result = {};
  const lines = envContent.split(/\r?\n/);
  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;
    const eqIdx = trimmed.indexOf('=');
    if (eqIdx <= 0) continue;
    const key = trimmed.slice(0, eqIdx).trim();
    let val = trimmed.slice(eqIdx + 1).trim();

    // Strip surrounding quotes
    if (
      (val.startsWith('"') && val.endsWith('"')) ||
      (val.startsWith("'") && val.endsWith("'"))
    ) {
      val = val.slice(1, -1);
    }

    result[key] = val;
  }

  // Merge with system process.env, preferring .env for specific variables
  const merged = { ...process.env, ...result };
  return { env: merged, envFileFound: fs.existsSync(envPath) };
}

/**
 * Execute command synchronously and inherit stdio
 */
export function runCommand(cmd, args, { cwd, env } = {}) {
  const quotedArgs = args.map((arg) => {
    if (typeof arg === 'string' && (arg.includes(' ') || arg.includes('\t')) && !arg.startsWith('"')) {
      return `"${arg}"`;
    }
    return arg;
  });

  const displayCmd = `${cmd} ${quotedArgs.join(' ')}`;
  logInfo(`Executing: ${displayCmd} (in ${cwd || process.cwd()})`);

  return new Promise((resolve, reject) => {
    const child = spawn(displayCmd, {
      cwd: cwd || process.cwd(),
      env: { ...process.env, ...env },
      stdio: 'inherit',
      shell: true,
    });

    child.on('close', (code) => {
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`Command failed with exit code ${code}: ${displayCmd}`));
      }
    });

    child.on('error', (err) => {
      reject(err);
    });
  });
}

/**
 * Safely kill running ICC-CE host process if any to avoid file locks
 */
export function killRunningHost() {
  if (process.platform === 'win32') {
    try {
      execSync('taskkill /IM InkCanvasForClass.exe /F', { stdio: 'ignore' });
      logInfo('Closed previously running InkCanvasForClass process to prevent file lock.');
    } catch {
      // Ignore if process was not running
    }
  }
}

/**
 * Locate InkCanvasForClass.exe in host build output
 */
export function findHostExe(hostRootDir, config = 'Debug') {
  const inkCanvasDir = path.join(hostRootDir, 'Ink Canvas', 'bin', config);
  if (!fs.existsSync(inkCanvasDir)) {
    throw new Error(`Host output directory does not exist: ${inkCanvasDir}`);
  }

  // Search recursively for InkCanvasForClass.exe
  const candidates = [];
  function search(dir) {
    const entries = fs.readdirSync(dir, { withFileTypes: true });
    for (const entry of entries) {
      const fullPath = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        search(fullPath);
      } else if (entry.name.toLowerCase() === 'inkcanvasforclass.exe') {
        const stats = fs.statSync(fullPath);
        candidates.push({ path: fullPath, dir, mtime: stats.mtimeMs });
      }
    }
  }

  search(inkCanvasDir);

  if (candidates.length === 0) {
    throw new Error(`InkCanvasForClass.exe not found under ${inkCanvasDir}. Please ensure the host project built successfully.`);
  }

  // Pick the newest one
  candidates.sort((a, b) => b.mtime - a.mtime);
  return candidates[0];
}

/**
 * Locate plugin build output directory
 */
export function findPluginOutputDir(pluginRootDir, config = 'Debug') {
  const binDir = path.join(pluginRootDir, 'bin', config);
  if (!fs.existsSync(binDir)) {
    throw new Error(`Plugin output directory does not exist: ${binDir}`);
  }

  // Search for directory containing ICC.CE.ENBX.dll
  const candidates = [];
  function search(dir) {
    const entries = fs.readdirSync(dir, { withFileTypes: true });
    for (const entry of entries) {
      const fullPath = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        const dllPath = path.join(fullPath, 'ICC.CE.ENBX.dll');
        if (fs.existsSync(dllPath)) {
          const stats = fs.statSync(dllPath);
          candidates.push({ dir: fullPath, mtime: stats.mtimeMs });
        }
        search(fullPath);
      }
    }
  }

  search(binDir);

  if (candidates.length === 0) {
    throw new Error(`ICC.CE.ENBX.dll not found under ${binDir}. Please ensure the plugin project built successfully.`);
  }

  candidates.sort((a, b) => b.mtime - a.mtime);
  return candidates[0].dir;
}

/**
 * Copy directory contents recursively
 */
export function copyDirectory(src, dest) {
  if (!fs.existsSync(dest)) {
    fs.mkdirSync(dest, { recursive: true });
  }

  fs.cpSync(src, dest, {
    recursive: true,
    force: true,
    filter: (source) => {
      // Don't copy icpx directory if it accidentally exists in source
      return !source.endsWith('.icpx');
    },
  });
}

/**
 * Read manifest.json from plugin root
 */
export function readManifest(pluginRootDir) {
  const manifestPath = path.join(pluginRootDir, 'manifest.json');
  if (!fs.existsSync(manifestPath)) {
    throw new Error(`manifest.json not found in ${pluginRootDir}`);
  }
  return JSON.parse(fs.readFileSync(manifestPath, 'utf-8'));
}

/**
 * Ensure plugin is authorized in host Configs/plugin_authorizations.json
 * to prevent modal confirmation prompt from blocking UI startup.
 */
export function ensurePluginAuthorized(hostExeDir, pluginId) {
  const configsDir = path.join(hostExeDir, 'Configs');
  if (!fs.existsSync(configsDir)) {
    fs.mkdirSync(configsDir, { recursive: true });
  }

  const authFile = path.join(configsDir, 'plugin_authorizations.json');
  let authData = { Version: 2, Plugins: [], Files: {} };
  if (fs.existsSync(authFile)) {
    try {
      authData = JSON.parse(fs.readFileSync(authFile, 'utf-8'));
      if (!Array.isArray(authData.Plugins)) {
        authData.Plugins = [];
      }
    } catch {
      // ignore
    }
  }

  if (!authData.Plugins.some((id) => id.toLowerCase() === pluginId.toLowerCase())) {
    authData.Plugins.push(pluginId);
    fs.writeFileSync(authFile, JSON.stringify(authData, null, 2), 'utf-8');
    logInfo(`Added "${pluginId}" to host trusted plugins to bypass authorization dialog.`);
  }
}

/**
 * Ensures the native launch-desktop.exe helper exists.
 * This helper uses CreateProcess with lpDesktop = "WinSta0\\Default"
 * to escape sandbox desktops (such as Antigravity IDE integrated terminals)
 * and display the host GUI on the user's interactive desktop.
 */
export function ensureDesktopLauncher(scriptsDir) {
  if (process.platform !== 'win32') {
    return null;
  }

  const exePath = path.join(scriptsDir, 'launch-desktop.exe');
  if (fs.existsSync(exePath)) {
    return exePath;
  }

  const csPath = path.join(scriptsDir, 'launch-desktop.cs');
  if (!fs.existsSync(csPath)) {
    return null;
  }

  const cscPath = 'C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe';
  if (fs.existsSync(cscPath)) {
    try {
      execSync(`"${cscPath}" /optimize /target:exe /out:"${exePath}" "${csPath}"`, { stdio: 'ignore' });
      if (fs.existsSync(exePath)) {
        return exePath;
      }
    } catch {
      // Ignore fallback
    }
  }

  return null;
}


