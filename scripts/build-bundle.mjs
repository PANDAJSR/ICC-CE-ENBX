import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import {
  loadEnv, runCommand, readManifest, ensurePluginAuthorized,
  logStep, logInfo, logSuccess, logError,
} from './common.mjs';

function sourceVersion(directory) {
  try {
    const git = (...args) => execFileSync('git', ['-C', directory, ...args], { encoding: 'utf8' }).trim();
    return { commit: git('rev-parse', 'HEAD'), modified: git('status', '--porcelain').length > 0 };
  } catch {
    return { commit: null, modified: null };
  }
}

async function main() {
  if (process.platform !== 'win32') throw new Error('The integrated bundle must be built on Windows.');
  const root = path.resolve(import.meta.dirname, '..');
  const { env } = loadEnv(root);
  const hostRoot = path.resolve(root, env.ICC_CE_PATH || '../iccce');
  const webRoot = path.resolve(root, env.WEB_EASINOTE_PATH || '../WebEasiNote');
  const hostProject = path.join(hostRoot, 'Ink Canvas', 'InkCanvasForClass.csproj');
  const magnifierProject = path.join(hostRoot, 'InkCanvas.LiquidGlassMagHost', 'InkCanvas.LiquidGlassMagHost.csproj');
  const sdkProject = path.join(hostRoot, 'InkCanvas.PluginSdk', 'InkCanvas.PluginSdk.csproj');
  const pluginProject = path.join(root, 'ICC.CE.ENBX.csproj');
  const runtime = process.argv[2] || 'win-x64';
  if (process.argv.length > 3 || !['win-x64', 'win-x86', 'win-arm64'].includes(runtime)) {
    throw new Error('Usage: pnpm build:bundle [win-x64|win-x86|win-arm64]');
  }
  for (const file of [hostProject, sdkProject, magnifierProject, path.join(webRoot, 'package.json')]) {
    if (!fs.existsSync(file)) throw new Error(`Required source file not found: ${file}. Check ICC_CE_PATH / WEB_EASINOTE_PATH in .env.`);
  }
  // The plugin project currently imports its SDK and web resources through sibling paths.
  // Fail explicitly rather than silently embed assets or an SDK from a different checkout.
  for (const [configured, sibling] of [[hostRoot, '../iccce'], [webRoot, '../WebEasiNote']]) {
    if (fs.realpathSync(configured).toLowerCase() !== fs.realpathSync(path.resolve(root, sibling)).toLowerCase()) {
      throw new Error('Bundle builds require sibling iccce and WebEasiNote repositories, matching ICC.CE.ENBX.csproj references.');
    }
  }
  const manifest = readManifest(root);
  const timestamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\..+/, '').replace('T', '-');
  const name = `ICC-CE-ENBX-${runtime}-${timestamp}`;
  const releaseDir = path.join(root, 'release');
  fs.mkdirSync(releaseDir, { recursive: true });
  const tempRoot = path.join(os.tmpdir(), 'opencode');
  fs.mkdirSync(tempRoot, { recursive: true });
  const temporary = fs.mkdtempSync(path.join(tempRoot, 'icc-ce-enbx-bundle-'));
  const bundle = path.join(temporary, name);
  const archive = path.join(releaseDir, `${name}.zip`);
  try {
    logStep(1, 5, 'Building current WebEasiNote sources...');
    await runCommand('pnpm', ['build'], { cwd: webRoot });
    if (!fs.existsSync(path.join(webRoot, 'dist', 'index.html'))) throw new Error('WebEasiNote dist/index.html was not generated.');

    logStep(2, 5, 'Building Release plugin with the local host SDK...');
    await runCommand('dotnet', ['build', pluginProject, '-c', 'Release', '-p:UseLocalPluginSdk=true'], { cwd: root });
    const projectXml = fs.readFileSync(pluginProject, 'utf8');
    const framework = projectXml.match(/<TargetFramework>([^<]+)<\/TargetFramework>/)?.[1];
    if (!framework) throw new Error('Plugin TargetFramework not found.');
    const pluginOutput = path.join(root, 'bin', 'Release', framework);
    const requiredPluginFiles = ['ICC.CE.ENBX.dll', 'manifest.json'];
    if (fs.existsSync(path.join(root, 'tools', 'SeewoImBridge', 'SeewoImBridge.csproj'))) {
      requiredPluginFiles.push('SeewoImBridge/SeewoImBridge.dll');
    }
    for (const file of requiredPluginFiles) {
      if (!fs.existsSync(path.join(pluginOutput, file))) throw new Error(`Missing plugin output: ${file}`);
    }

    logStep(3, 5, `Publishing self-contained host (${runtime})...`);
    const platform = { 'win-x64': 'AnyCPU', 'win-x86': 'x86', 'win-arm64': 'ARM64' }[runtime];
    await runCommand('dotnet', [
      'publish', hostProject, '-c', 'Release', '-r', runtime, '--self-contained', 'true',
      '--output', bundle, `-p:Platform=${platform}`, '-p:PublishSingleFile=true',
      '-p:PublishTrimmed=false', '-p:IncludeNativeLibrariesForSelfExtract=true',
    ], { cwd: hostRoot });
    if (!fs.existsSync(path.join(bundle, 'InkCanvasForClass.exe'))) throw new Error('Published host executable is missing.');
    // The host's copy target does not include the RID subdirectory used by publish.
    const magnifierOutput = path.join(temporary, 'magnifier');
    await runCommand('dotnet', [
      'publish', magnifierProject, '-c', 'Release', '-r', runtime, '--self-contained', 'true',
      '--output', magnifierOutput, `-p:Platform=${platform}`, '-p:PublishSingleFile=true',
      '-p:PublishTrimmed=false', '-p:IncludeNativeLibrariesForSelfExtract=true',
    ], { cwd: hostRoot });
    fs.copyFileSync(path.join(magnifierOutput, 'InkCanvas.LiquidGlassMagHost.exe'), path.join(bundle, 'InkCanvas.LiquidGlassMagHost.exe'));

    logStep(4, 5, 'Adding plugin and build information...');
    const pluginDir = path.join(bundle, 'Plugins', manifest.Id);
    fs.cpSync(pluginOutput, pluginDir, {
      recursive: true,
      filter: (file) => !['configs', 'pluginlogs', 'logs', 'cache', 'webview2'].includes(path.basename(file).toLowerCase())
        && !/\.(icpx|pdb|log)$/i.test(file),
    });
    ensurePluginAuthorized(bundle, manifest.Id);
    fs.writeFileSync(path.join(bundle, 'bundle-info.json'), JSON.stringify({
      builtAt: new Date().toISOString(), configuration: 'Release', runtime,
      selfContained: true, pluginId: manifest.Id,
      sources: { host: sourceVersion(hostRoot), plugin: sourceVersion(root), web: sourceVersion(webRoot) },
    }, null, 2));
    for (const [directory, target] of [[root, 'LICENSE-ENBX'], [hostRoot, 'LICENSE-ICC-CE'], [webRoot, 'LICENSE-WebEasiNote']]) {
      if (fs.existsSync(path.join(directory, 'LICENSE'))) fs.copyFileSync(path.join(directory, 'LICENSE'), path.join(bundle, target));
    }
    fs.writeFileSync(path.join(bundle, '启动 ICC-CE.cmd'), '@echo off\r\ncd /d "%~dp0"\r\nstart "" "InkCanvasForClass.exe" --show\r\n');
    fs.writeFileSync(path.join(bundle, '整合包说明.txt'), [
      'ICC-CE + ENBX 插件源码测试整合包',
      '请先完整解压，再双击“启动 ICC-CE.cmd”。工具 -> 打开可选择 ENBX 课件。',
      '已附带宿主 .NET 10 运行时；仍需安装 Microsoft Edge WebView2 Runtime。',
      '希沃手机传图仍需本机安装官方希沃白板 5 组件及 x86 .NET 10 运行时。',
      '本包预先信任所附 ENBX 插件。首次运行会生成配置、缓存和日志。',
      '这是本地源码测试版。反馈问题请附 bundle-info.json 和 PluginLogs 下的相关日志。',
      '',
    ].join('\r\n'));

    logStep(5, 5, 'Creating ZIP archive...');
    // Pass paths through environment variables so PowerShell never interprets them as code.
    await runCommand('powershell', ['-NoProfile', '-Command', '"Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::CreateFromDirectory($env:ENBX_BUNDLE_SOURCE, $env:ENBX_BUNDLE_ZIP)"'], {
      cwd: root, env: { ENBX_BUNDLE_SOURCE: bundle, ENBX_BUNDLE_ZIP: archive },
    });
    logSuccess(`Integrated bundle: ${archive}`);
    logInfo('Extract the ZIP and run 启动 ICC-CE.cmd.');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

main().catch((error) => {
  logError(error.message);
  process.exitCode = 1;
});
