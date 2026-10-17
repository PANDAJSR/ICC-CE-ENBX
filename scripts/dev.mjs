import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import {
  colors,
  logStep,
  logSuccess,
  logInfo,
  logWarn,
  logError,
  loadEnv,
  runCommand,
  killRunningHost,
  findHostExe,
  findPluginOutputDir,
  copyDirectory,
  readManifest,
  ensurePluginAuthorized,
  ensureDesktopLauncher,
} from './common.mjs';

const TOTAL_STEPS = 5;

async function main() {
  const pluginRootDir = path.resolve(import.meta.dirname, '..');
  console.log(`${colors.magenta}${colors.bold}=== ICC-CE-ENBX Development Launcher ===${colors.reset}`);
  console.log(`${colors.gray}Working directory: ${pluginRootDir}${colors.reset}\n`);

  // 0. Load environment variables
  const { env, envFileFound } = loadEnv(pluginRootDir);
  if (!envFileFound) {
    logWarn(`No .env file found in ${pluginRootDir}. Falling back to default or system environment variables.`);
  }

  const withoutPlugin = process.argv.includes('--no-plugin') || env.NO_PLUGIN === '1';

  const rawHostPath = env.ICC_CE_PATH || '../iccce';
  const hostRootDir = path.resolve(pluginRootDir, rawHostPath);
  const rawWebPath = env.WEB_EASINOTE_PATH || '../WebEasiNote';
  const webRootDir = path.resolve(pluginRootDir, rawWebPath);
  const buildConfig = env.BUILD_CONFIGURATION || 'Debug';

  logInfo(`Host repo path (ICC_CE_PATH): ${colors.cyan}${hostRootDir}${colors.reset}`);
  if (withoutPlugin) {
    logWarn('Running without ENBX plugin (--no-plugin specified).');
  } else {
    logInfo(`Web frontend path (WEB_EASINOTE_PATH): ${colors.cyan}${webRootDir}${colors.reset}`);
  }
  logInfo(`Build Configuration: ${colors.cyan}${buildConfig}${colors.reset}`);

  // Validate host path
  const hostCsproj = path.join(hostRootDir, 'Ink Canvas', 'InkCanvasForClass.csproj');
  if (!fs.existsSync(hostCsproj)) {
    logError(`Could not find InkCanvasForClass.csproj at: ${hostCsproj}`);
    logError(`Please set ICC_CE_PATH in your .env file to the root directory of the ICC-CE repository.`);
    process.exit(1);
  }

  // Pre-cleanup running host to unlock files
  killRunningHost();

  let pluginOutputDir = null;
  if (!withoutPlugin) {
    // 1. Build WebEasiNote frontend
    logStep(1, TOTAL_STEPS, 'Building WebEasiNote frontend...');
    if (!fs.existsSync(path.join(webRootDir, 'package.json'))) {
      logWarn(`WebEasiNote package.json not found at ${webRootDir}. Skipping frontend build.`);
    } else {
      await runCommand('pnpm', ['build'], { cwd: webRootDir });
      logSuccess('WebEasiNote frontend built successfully.');
    }

    // 2. Build ICC-CE-ENBX Plugin
    logStep(2, TOTAL_STEPS, `Building ICC-CE-ENBX plugin (${buildConfig})...`);
    const pluginCsproj = path.join(pluginRootDir, 'ICC.CE.ENBX.csproj');
    await runCommand('dotnet', ['build', pluginCsproj, '-c', buildConfig], { cwd: pluginRootDir });
    pluginOutputDir = findPluginOutputDir(pluginRootDir, buildConfig);
    logSuccess(`Plugin built successfully: ${pluginOutputDir}`);
  } else {
    logInfo('Skipping plugin and frontend build (--no-plugin mode).');
  }

  // 3. Build ICC-CE Host
  logStep(3, TOTAL_STEPS, `Building ICC-CE host (${buildConfig})...`);
  await runCommand('dotnet', ['build', hostCsproj, '-c', buildConfig], { cwd: hostRootDir });
  const hostExeInfo = findHostExe(hostRootDir, buildConfig);
  logSuccess(`Host built successfully: ${hostExeInfo.path}`);

  // 4. Synchronize plugin files to host Plugins directory
  const manifest = readManifest(pluginRootDir);
  const pluginId = manifest.Id || 'com.pandajsr.icc-ce-enbx';
  const targetPluginDir = path.join(hostExeInfo.dir, 'Plugins', pluginId);

  if (!withoutPlugin && pluginOutputDir) {
    logStep(4, TOTAL_STEPS, 'Deploying plugin to host Plugins directory...');
    logInfo(`Target plugin folder: ${targetPluginDir}`);
    copyDirectory(pluginOutputDir, targetPluginDir);
    ensurePluginAuthorized(hostExeInfo.dir, pluginId);
    logSuccess(`Plugin deployed to host successfully.`);
  } else {
    // In no-plugin mode, ensure the plugin is removed from host Plugins folder if present
    if (fs.existsSync(targetPluginDir)) {
      logInfo(`Removing plugin from host Plugins folder: ${targetPluginDir}`);
      fs.rmSync(targetPluginDir, { recursive: true, force: true });
    }
  }

  // 5. Launch ICC-CE Host
  logStep(5, TOTAL_STEPS, withoutPlugin ? 'Launching ICC-CE host without plugin...' : 'Launching ICC-CE host with plugin...');

  const launchEnv = {
    ...process.env,
    ...env,
  };

  if (!withoutPlugin && (env.ICC_CE_ENBX_DEVTOOLS === '1' || env.ICC_CE_ENBX_DEVTOOLS === undefined)) {
    launchEnv.ICC_CE_ENBX_DEVTOOLS = '1';
    logInfo('WebView2 DevTools enabled (press F12 or Ctrl+Shift+I in WebView).');
  }

  // Parse launch arguments: default to --show to ensure floating toolbar is unfolded and visible
  const hostArgs = [];
  if (env.HOST_ARGS) {
    hostArgs.push(...env.HOST_ARGS.split(/\s+/).filter(Boolean));
  } else {
    hostArgs.push('--show');
  }

  // Optional test courseware file to open on startup
  if (!withoutPlugin && env.ENBX_FILE && fs.existsSync(env.ENBX_FILE)) {
    hostArgs.push(path.resolve(env.ENBX_FILE));
    logInfo(`Auto-opening ENBX courseware: ${path.resolve(env.ENBX_FILE)}`);
  }

  logInfo(`Starting: ${hostExeInfo.path} ${hostArgs.join(' ')}`);

  const launcherExe = ensureDesktopLauncher(import.meta.dirname);
  const child = launcherExe
    ? spawn(launcherExe, [hostExeInfo.dir, hostExeInfo.path, ...hostArgs], {
        cwd: hostExeInfo.dir,
        env: launchEnv,
        stdio: 'inherit',
        detached: false,
      })
    : spawn(hostExeInfo.path, hostArgs, {
        cwd: hostExeInfo.dir,
        env: launchEnv,
        stdio: 'inherit',
        detached: false,
      });

  child.on('close', (code) => {
    console.log(`\n${colors.cyan}[ICC-CE] Process exited with code ${code}.${colors.reset}`);
    process.exit(code ?? 0);
  });

  child.on('error', (err) => {
    logError(`Failed to launch ICC-CE: ${err.message}`);
    process.exit(1);
  });

  // Handle Ctrl+C
  process.on('SIGINT', () => {
    logInfo('Received SIGINT. Closing ICC-CE...');
    try {
      child.kill('SIGINT');
    } catch {
      // Ignore
    }
  });
}

main().catch((err) => {
  logError(`Fatal error: ${err.message}`);
  process.exit(1);
});
