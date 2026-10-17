import path from 'node:path';
import fs from 'node:fs';
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
} from './common.mjs';

const TOTAL_STEPS = 4;

async function main() {
  const pluginRootDir = path.resolve(import.meta.dirname, '..');
  console.log(`${colors.magenta}${colors.bold}=== ICC-CE-ENBX Build & Deploy ===${colors.reset}`);
  console.log(`${colors.gray}Working directory: ${pluginRootDir}${colors.reset}\n`);

  // 0. Load environment variables
  const { env, envFileFound } = loadEnv(pluginRootDir);
  if (!envFileFound) {
    logWarn(`No .env file found in ${pluginRootDir}. Falling back to default or system environment variables.`);
  }

  const rawHostPath = env.ICC_CE_PATH || '../iccce';
  const hostRootDir = path.resolve(pluginRootDir, rawHostPath);
  const rawWebPath = env.WEB_EASINOTE_PATH || '../WebEasiNote';
  const webRootDir = path.resolve(pluginRootDir, rawWebPath);
  const buildConfig = env.BUILD_CONFIGURATION || 'Release';

  logInfo(`Host repo path (ICC_CE_PATH): ${colors.cyan}${hostRootDir}${colors.reset}`);
  logInfo(`Web frontend path (WEB_EASINOTE_PATH): ${colors.cyan}${webRootDir}${colors.reset}`);
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
  const pluginOutputDir = findPluginOutputDir(pluginRootDir, buildConfig);
  logSuccess(`Plugin built successfully: ${pluginOutputDir}`);

  // 3. Build ICC-CE Host
  logStep(3, TOTAL_STEPS, `Building ICC-CE host (${buildConfig})...`);
  await runCommand('dotnet', ['build', hostCsproj, '-c', buildConfig], { cwd: hostRootDir });
  const hostExeInfo = findHostExe(hostRootDir, buildConfig);
  logSuccess(`Host built successfully: ${hostExeInfo.path}`);

  // 4. Synchronize plugin files to host Plugins directory
  logStep(4, TOTAL_STEPS, 'Deploying plugin to host Plugins directory...');
  const manifest = readManifest(pluginRootDir);
  const pluginId = manifest.Id || 'com.pandajsr.icc-ce-enbx';
  const targetPluginDir = path.join(hostExeInfo.dir, 'Plugins', pluginId);

  logInfo(`Target plugin folder: ${targetPluginDir}`);
  copyDirectory(pluginOutputDir, targetPluginDir);
  ensurePluginAuthorized(hostExeInfo.dir, pluginId);
  logSuccess(`Plugin deployed to host successfully: ${targetPluginDir}`);
  console.log(`\n${colors.green}${colors.bold}Build and deployment completed successfully!${colors.reset}`);
}

main().catch((err) => {
  logError(`Fatal error: ${err.message}`);
  process.exit(1);
});
