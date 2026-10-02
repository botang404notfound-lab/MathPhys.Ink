/**
 * fetch-geogebra-bundle.mjs —— 把 GeoGebra 的 codebase 完整镜像到本地（M7.5 S1）
 *
 * 为什么需要这个工具，而不是"下载官方离线包"：
 *   GeoGebra 官方的离线包在 download.geogebra.org（package/geogebra-math-apps-bundle），
 *   而该域名在本机**网络层不可达**：端口 80 与 443 均 ECONNRESET。
 *   可达的是 www.geogebra.org/apps/<版本>/ —— 这本来就是官网自己加载应用的 codebase，
 *   所以它需要的文件一定都在这里。本工具按 GWT 的加载规则把需要的文件全抓下来。
 *
 * ★★ 与上一版的关键区别（M7.5 S1 实测教训）：
 *   只抓 web3d/ 下的 JS 排列（permutation + 分片）是不够的。
 *   web3d 的排列在运行时还会**动态注入**一堆**根级兄弟资源**：
 *     css/bundles/simple-bundle.css、css/bundles/bundle.css、
 *     css/keyboard-styles.css、css/greek-font.css、css/fonts.css
 *   这些 CSS 里又用 url(...) 引着 .woff2 / .ttf 字体、.png 图标……
 *   任一 404，面板不会报任何 JS 错误，而是**静默卡死**——applet 永远不 ready，
 *   onLoad 回调永远不来。这正是 M7.5 S1 第一次实测"装载器调了、然后 25 秒超时"的根因。
 *
 *   所以本工具改成**递归爬虫**：
 *     ① 抓 web3d.nocache.js → 解析 permutation 强名（不硬编码，升级自动跟着走）；
 *     ② 抓排列本体 + clear.cache.gif + deferredjs 分片（沿用旧逻辑）；
 *     ③ 把以上文件当作种子，BFS 扫描其中所有「同域资源 URL」——
 *        绝对地址 https://www.geogebra.org/apps/<版本>/...
 *        相对地址（按文件所在目录解析）
 *        CSS 里的 url(...) 引用
 *        并下载它们；
 *     ④ EXTRA 里可以手动补"运行时实测才发现缺的文件"（反馈闭环：跑一次冒烟 → 看缺什么 → 补进来）。
 *
 *   凡是路径落在 https://www.geogebra.org/apps/<版本>/ 下、且带资源扩展名的，都会被抓取，
 *   落到本地 geogebra/ 的对应相对路径（与虚拟域名映射 geogebra.local → geogebra/ 对齐）。
 *
 * 用法：
 *   node tools/fetch-geogebra-bundle.mjs
 *   node tools/fetch-geogebra-bundle.mjs --version 5.4.920.0 --out <目录>
 */

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const ROOT = path.resolve(import.meta.dirname, '..');

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
}

const VERSION = arg('version', '5.4.920.0');
const OUT = path.resolve(arg('out', path.join(ROOT, 'src', 'MathPhys.Ink', 'Assets', 'geogebra')));
const BASE = `https://www.geogebra.org/apps/${VERSION}/`;
const HEADERS = {
  // 不带 Referer 会 403 —— 官网对 apps/ 有防盗链
  'Referer': 'https://www.geogebra.org/geometry',
  'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) MathPhys-Ink/1.0',
};

/** 上限：防止误爬到无关大文件（素材 .ggb、帮助文档等）。 */
const MAX_FILES = 6000;
const MAX_BYTES = 600 * 1024 * 1024;

/** 只跟随带这些扩展名的 URL —— 避免爬进 .html/.php 页面把无关内容拉进来。 */
const ASSET_EXT = new Set([
  'css', 'js', 'mjs', 'woff', 'woff2', 'ttf', 'otf', 'eot',
  'png', 'gif', 'jpg', 'jpeg', 'svg', 'webp', 'ico',
  'mp3', 'wav', 'json', 'wasm',
]);

/** 运行时实测才发现缺的文件（反馈闭环：冒烟脚本报缺 → 这里补）。 */
const EXTRA = [
  // 2026-09-22 S1 实测缺的根级 CSS（web3d 排列动态注入，缺失会静默卡死）
  'css/bundles/simple-bundle.css',
  'css/bundles/bundle.css',
  'css/keyboard-styles.css',
  'css/greek-font.css',
  'css/fonts.css',
  // 2026-09-22 S1 实测缺的中文界面语言包（panel 设了 language=zh_CN，
  // 缺失时 GeoGebra 回退英文，功能不受影响但界面应是中文）。
  'web3d/js/properties_keys_zh-CN.js',
];

// ---------------------------------------------------------------- 爬取状态

const visited = new Set();   // 已入队（或已抓）的规范化 URL
const queue = [];            // 待抓 URL
const manifest = [];
let totalBytes = 0;

function normalize(u) {
  try {
    const url = new URL(u, BASE);
    // 去掉查询串：GeoGebra 资源不带 cache-busting，去重更稳。
    url.search = '';
    url.hash = '';
    return url.href;
  } catch {
    return null;
  }
}

function isAssetUrl(u) {
  if (!u.startsWith(BASE)) return false;
  const pathname = u.slice(BASE.length);
  const ext = pathname.includes('.') ? pathname.slice(pathname.lastIndexOf('.') + 1).toLowerCase() : '';
  return ASSET_EXT.has(ext);
}

function enqueue(u) {
  const n = normalize(u);
  if (!n) return;
  if (visited.has(n)) return;
  if (!isAssetUrl(n)) return;
  visited.add(n);
  queue.push(n);
}

async function get(url) {
  try {
    const response = await fetch(url, { headers: HEADERS });
    if (!response.ok) return null;
    return Buffer.from(await response.arrayBuffer());
  } catch {
    return null;
  }
}

/** 从已抓文件文本里抽出所有同域资源引用。 */
function extractRefs(text, baseUrl) {
  const refs = new Set();

  // ① 绝对同域地址
  const ABS = /https?:\/\/www\.geogebra\.org\/apps\/(?:[^\s"'()\\]+)/g;
  let m;
  while ((m = ABS.exec(text)) !== null) refs.add(m[0]);

  // ② 相对路径（带资源扩展名）：'css/bundles/foo.css'、"js/lib/bar.js" 等
  const REL = /['"]((?:[A-Za-z0-9_.\-]+\/)+[A-Za-z0-9_.\-]+\.[A-Za-z0-9]+)['"]/g;
  while ((m = REL.exec(text)) !== null) {
    try { refs.add(new URL(m[1], baseUrl).href); } catch { /* ignore */ }
  }

  // ③ CSS 的 url(...) 引用（字体 / 图标 / 图片）
  const CSS = /url\(\s*['"]?([^'")]+)['"]?\s*\)/g;
  while ((m = CSS.exec(text)) !== null) {
    const raw = m[1].trim();
    if (raw.startsWith('data:') || raw.startsWith('#')) continue;
    try { refs.add(new URL(raw, baseUrl).href); } catch { /* ignore */ }
  }

  return refs;
}

function save(url, buffer) {
  const rel = decodeURIComponent(url.slice(BASE.length));   // apps/<版本>/ 之后的相对路径
  const target = path.join(OUT, rel.split('/').join(path.sep));
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, buffer);
  totalBytes += buffer.length;
  manifest.push({
    file: rel,
    bytes: buffer.length,
    sha256: crypto.createHash('sha256').update(buffer).digest('hex'),
  });
  console.log(`  [ok] ${rel}  ${buffer.length} bytes`);
}

// ---------------------------------------------------------------- 主流程

async function main() {
  console.log(`GeoGebra codebase 完整镜像：版本 ${VERSION}`);
  console.log(`  源：${BASE}`);
  console.log(`  目标：${OUT}`);
  console.log('');

  // ---- 1) 装载器 ----
  const loader = await get(`${BASE}web3d/web3d.nocache.js`);
  if (!loader) {
    console.error('  [FAIL] 抓不到 web3d/web3d.nocache.js —— 版本号写错了？还是网络被掐了？');
    process.exitCode = 1;
    return;
  }
  const loaderText = loader.toString('utf8');
  save(`${BASE}web3d/web3d.nocache.js`, loader);

  // ---- 2) 从装载器里解析 permutation 强名（不硬编码） ----
  const strongName = (loaderText.match(/'([0-9A-F]{32})'/) || [])[1];
  if (!strongName) {
    console.error('  [FAIL] 从 nocache.js 解析不出 permutation 强名（32 位十六进制）。');
    process.exitCode = 1;
    return;
  }
  console.log(`  解析出强名 ${strongName}`);

  // ---- 3) 入队：排列本体 + 探测图 + 代码分片 + 手动补的 EXTRA ----
  enqueue(`${BASE}web3d/${strongName}.cache.js`);
  enqueue(`${BASE}web3d/clear.cache.gif`);
  for (const e of EXTRA) enqueue(`${BASE}${e}`);

  // ---- 4) BFS 爬取 ----
  console.log('  ---- 递归爬取（同域资源 + CSS url()）----');
  let idx = 0;
  while (queue.length > 0) {
    const url = queue.shift();
    idx++;
    const buf = await get(url);
    if (!buf) {
      console.log(`  [--] 抓取失败或 404：${url.slice(BASE.length)}`);
      continue;
    }
    save(url, buf);

    // 只扫描文本类资源里的引用（二进制跳过，省 CPU）
    const ext = url.slice(url.lastIndexOf('.') + 1).toLowerCase();
    if (ext === 'js' || ext === 'mjs' || ext === 'css' || ext === 'json' || ext === 'html') {
      const refs = extractRefs(buf.toString('utf8'), url);
      for (const r of refs) enqueue(r);
    }

    if (manifest.length >= MAX_FILES || totalBytes >= MAX_BYTES) {
      console.log('  [!] 达到上限，停止爬取（可能是意外的大目录，请检查）。');
      break;
    }
  }
  console.log(`  共抓取 ${manifest.length} 个文件，合计 ${(totalBytes / 1048576).toFixed(2)} MB`);

  // ---- 5) 清单 ----
  const manifestPath = path.join(OUT, 'bundle-manifest.txt');
  const lines = [
    'GeoGebra codebase 本地镜像清单（完整递归镜像）',
    '='.repeat(60),
    `版本：${VERSION}`,
    `来源：${BASE}`,
    `抓取时间：${new Date().toISOString()}`,
    `文件数：${manifest.length}`,
    `合计：${totalBytes} bytes（${(totalBytes / 1048576).toFixed(2)} MB）`,
    '',
    '★ 未包含 giac（CAS 计算引擎，giac.js / giac.wasm，体积很大）。',
    '  几何／代数演示不需要它；若哪天要在面板里做符号计算，得另抓这两个文件。',
    '★ 许可：GeoGebra 为 GPL v3 且官方许可仅限非商业用途，本项目仅个人教学自用。',
    '  详见工程根目录 assets/第三方许可与署名.txt。',
    '',
    '文件清单（大小 / SHA256）：',
    ...manifest.map(m => `  ${String(m.bytes).padStart(10)}  ${m.sha256}  ${m.file}`),
    '',
  ];
  fs.writeFileSync(manifestPath, lines.join('\n'), 'utf8');
  console.log(`清单：${manifestPath}`);
}

await main();
