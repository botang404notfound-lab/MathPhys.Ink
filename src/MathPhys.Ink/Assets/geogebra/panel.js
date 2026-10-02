/*
 * panel.js —— 正式面板：离线载入本地 GeoGebra codebase（M7.5 S1）
 *
 * 这一页的角色是**宿主页**：它自己不含任何 GeoGebra 代码，
 * 只做三件事：
 *   ① 摆好一个 <article class="appletParameters" data-param-*="…">，
 *      把「要什么样的应用」写在属性上；
 *   ② 等 GeoGebra 的 GWT 装载器导出了 renderGGBElement 之后，调它渲染；
 *   ③ 应用就绪后，才向宿主报 ready（就绪闸门的放行信号）。
 *
 * ★★ 为什么不用官方的 deployggb.js：
 *   它内置「本地 codebase 失败就悄悄回退到 www.geogebra.org」的逻辑。
 *   对联网开发机是便利，对教室断网的一体机就是灾难 ——
 *   表现为**加载很久然后白屏，且一行错误都不报**，正好是这个项目最怕的静默失败。
 *   自己写 loader 只有 100 行，每一步都在我们手里，失败也能变成一条明确的中文提示。
 *
 * ★★ 就绪信号为什么要"三重保险"：
 *   GWT 侧的可行回调路径不止一条（renderGGBElement 的第二参数 / window.ggbAppletOnLoad /
 *   全局 window.ggbApplet），不同小版本的 GeoGebra 走的不是同一条。
 *   这里三条都挂上，谁先到用谁 —— 因为「应用起来了但宿主不知道」会直接表现成白板卡死。
 */
(function (global) {
  'use strict';

  // ------------------------------------------------------------------ 配置

  /** 应用形态。classic = 代数区 + 绘图区，物理教学最需要的两样都在里面。 */
  var APP_NAME = 'classic';

  /** 视图组合。A=代数区，G=绘图区。★ 不含 CAS —— 我们没镜像 giac 计算引擎。 */
  var PERSPECTIVE = 'AG';

  /** 界面上没用到的应用形态参数（尺寸在 boot 时按容器校正）。 */
  var DEFAULT_WIDTH = 1280;
  var DEFAULT_HEIGHT = 720;

  /** 载入超时（毫秒）。一体机上首次启动要解压 21 MB 脚本，给得宽松一点。 */
  var LOAD_TIMEOUT_MS = 25000;

  // ------------------------------------------------------------------ 状态

  var api = null;                 // GeoGebra JS API 对象
  var readySignaled = false;
  var loadTimer = null;
  var startedAt = Date.now();
  var logs = [];

  /** 是否处于发布冒烟自测（URL 带 ?selftest=1）。 */
  var isSelfTest = /[?&]selftest=(1|2)\b/.test(global.location ? global.location.search : '');
  /** 是否处于 S2 发布冒烟（URL 带 ?selftest=2）：每执行一条命令都回传对象清单，供断言。 */
  var isSelfTest2 = /[?&]selftest=2\b/.test(global.location ? global.location.search : '');

  // ★ GeoGebra 的 onLoad 回调有两条路径（不同小版本走的不一样），都接上：
  //   ① 全局 ggbOnInitGlobal(id, api) —— 由 article 的 data-param-ggbOnInit 指定函数名；
  //   ② renderGGBElement 第二参数 appletOnLoad(api) —— 在 boot() 里传。
  //   两条都收敛到 adopt()，幂等。
  global.ggbOnInitGlobal = function (id, api) {
    log('★ ggbOnInitGlobal 被调用（id=' + (id || '') + ' typeof=' + (api ? typeof api : 'null') + '）');
    adopt(api);
  };

  // 自测模式把 GeoGebra 自己的 console.error/warn 也回传宿主，
  // 这样"applet 初始化卡住"时能看到 GGB 内部到底报了什么。
  if (isSelfTest && global.console) {
    var _err = global.console.error, _warn = global.console.warn;
    global.console.error = function () {
      var txt = '[GGB.error] ' + Array.prototype.join.call(arguments, ' ');
      geoBridge().post('diagnostic', { text: txt });
      if (_err) _err.apply(global.console, arguments);
    };
    global.console.warn = function () {
      var txt = '[GGB.warn] ' + Array.prototype.join.call(arguments, ' ');
      geoBridge().post('diagnostic', { text: txt });
      if (_warn) _warn.apply(global.console, arguments);
    };
  }

  // ★★ 排障探针（仅 selftest）：把"applet 卡住"变成可定位的日志。
  //   资源加载错误（<script>/<img>/worker）**不冒泡**，普通 window.onerror 抓不到，
  //   必须用捕获阶段监听；GeoGebra 的 GWT 装载器就是靠动态注入 <script> 拉分片，
  //   任一分片 404 / MIME 错 / 被拦 → 这里能看到。
  if (isSelfTest) {
    global.addEventListener('error', function (ev) {
      if (!ev) return;
      var t = ev.target;
      if (t && t !== global && t !== global.document) {
        var src = (t.src || t.href || (t.location && t.location.href) || '');
        log('[resource.error] ' + (t.tagName || (t.constructor && t.constructor.name) || '?') + ' → ' + src + (ev.message ? ' (' + ev.message + ')' : ''));
      } else if (ev.message) {
        log('[script.error] ' + ev.message + (ev.filename ? ' @ ' + ev.filename + ':' + ev.lineno : ''));
      }
    }, true);

    global.addEventListener('unhandledrejection', function (ev) {
      var r = ev && ev.reason;
      log('[unhandledrejection] ' + (r && (r.stack || r.message) || String(r)));
    });

    // 环境探针：crossOriginIsolated / SharedArrayBuffer 决定 GeoGebra 能不能用多线程内核。
    try {
      var _sab = (typeof global.SharedArrayBuffer !== 'undefined');
      log('[env] crossOriginIsolated=' + global.crossOriginIsolated
          + ' SharedArrayBuffer=' + _sab
          + ' ua=' + (global.navigator && global.navigator.userAgent));
    } catch (e) { /* ignore */ }

    // Worker 错误钩子：GeoGebra 内核跑在 Worker 里，worker 脚本加载失败不会冒泡到 window。
    if (global.Worker) {
      var _Worker = global.Worker;
      global.Worker = function (scriptURL, options) {
        var w = new _Worker(scriptURL, options);
        w.addEventListener('error', function (e) { log('[worker.error] ' + scriptURL + ' ' + (e && e.message || '')); });
        w.addEventListener('messageerror', function () { log('[worker.messageerror] ' + scriptURL); });
        return w;
      };
      global.Worker.prototype = _Worker.prototype;
      try { global.Worker.prototype.constructor = global.Worker; } catch (e) { /* ignore */ }
    }
  }

  function log(text) {
    var line = '[panel +' + Math.round((Date.now() - startedAt) / 100) / 10 + 's] ' + text;
    logs.push(line);
    if (global.console && console.log) console.log(line);

    // 仅自测模式把面板内部日志回传宿主（写到日志文件），方便冒烟脚本看到「渲染调用 / 回调」实况。
    // 正常面板不回传，避免污染运行日志。
    if (isSelfTest) geoBridge().post('diagnostic', { text: line });
  }

  function byId(id) {
    return global.document ? global.document.getElementById(id) : null;
  }

  /** 把内部消息序列化成宿主可读的一行（ExecuteScriptAsync 只能回字符串）。 */
  function json(value) {
    try {
      return JSON.stringify(value);
    } catch (e) {
      return '"(序列化失败)"';
    }
  }

  // ------------------------------------------------------------------ 遮罩

  /**
   * 遮罩三种形态：loading / hidden / failed。
   * ★ 失败态必须把原因写在屏幕上 —— 教室里没有开发者控制台可看。
   */
  function setVeil(mode, title, detail) {
    var veil = byId('veil');
    if (!veil) return;

    if (mode === 'hidden') {
      veil.className = 'veil hidden';
      return;
    }

    veil.className = 'veil' + (mode === 'failed' ? ' failed' : '');

    var titleEl = byId('veilTitle');
    var detailEl = byId('veilDetail');
    if (titleEl) titleEl.textContent = title || '';
    if (detailEl) detailEl.textContent = detail || '';
  }

  /** 加载中：每秒刷新一次已等待时长，让用户知道没死。 */
  function startLoadingTicker() {
    var timer = setInterval(function () {
      if (api || readySignaled) { clearInterval(timer); return; }

      var elapsed = Math.round((Date.now() - startedAt) / 1000);
      var detailEl = byId('veilDetail');
      if (detailEl) detailEl.textContent = '已等待 ' + elapsed + ' 秒（本地离线载入，无需联网）';
    }, 1000);
  }

  // ------------------------------------------------------------------ 尺寸

  var lastSize = { w: 0, h: 0 };

  /** 让 GeoGebra 铺满可用区域。 */
  function fit() {
    if (!api) return;

    var stage = byId('stage');
    if (!stage) return;

    var w = Math.max(400, stage.clientWidth | 0);
    var h = Math.max(300, stage.clientHeight | 0);

    if (w === lastSize.w && h === lastSize.h) return;
    lastSize = { w: w, h: h };

    try {
      if (typeof api.setSize === 'function') {
        api.setSize(w, h);
      } else {
        // 老版本的 API 是两个独立方法。
        if (typeof api.setWidth === 'function') api.setWidth(w);
        if (typeof api.setHeight === 'function') api.setHeight(h);
      }
      log('尺寸调整为 ' + w + '×' + h);
    } catch (e) {
      geoBridge().reportError('调整 GeoGebra 尺寸', e);
    }
  }

  // ------------------------------------------------------------------ 桥接

  function geoBridge() {
    return global.GeoGebraBridge || {
      post: function () { return false; },
      reportError: function () { },
      signalReady: function () { return false; },
      resignalReady: function () { return false; },
      signalLoadFailed: function () { }
    };
  }

  // ------------------------------------------------------------------ 就绪

  function ggbVersion() {
    if (!api) return '';
    try {
      return (typeof api.getVersion === 'function') ? String(api.getVersion()) : '';
    } catch (e) {
      return '';
    }
  }

  /** 应用真的能用了 —— 向宿主报 ready，放行它排队中的命令。 */
  function signalReady() {
    if (readySignaled || !api) return;
    readySignaled = true;

    if (loadTimer) { clearTimeout(loadTimer); loadTimer = null; }

    // 强制校正一次尺寸：万一 GeoGebra 没完全照 data-param 的尺寸渲染
    // （它自己会算一个最小宽度），这里再对齐一次真实容器。
    lastSize = { w: 0, h: 0 };
    fit();
    setVeil('hidden');
    hookUpdateListener();

    // ★ 发布冒烟自测：仅当 URL 带 ?selftest=1 时画一个圆 + 一个自由点，
    //   把"建出了几个对象"回传给宿主（写进日志）→ 冒烟脚本据此断言"断网可画圆/拖点"。
    if (/[?&]selftest=1\b/.test(global.location ? global.location.search : '')) {
      runSelfTest();
    }

    var guard = global.OfflineGuard;
    var violationCount = guard ? guard.violationCount() : 0;

    geoBridge().signalReady({
      app: APP_NAME,
      perspective: PERSPECTIVE,
      href: String(global.location ? global.location.href : ''),
      title: String(global.document ? global.document.title : ''),
      ggbVersion: ggbVersion(),
      width: lastSize.w,
      height: lastSize.h,
      offlineViolations: violationCount,
      elapsedMs: Date.now() - startedAt
    });

    log('已向宿主报 ready（GeoGebra ' + ggbVersion() + '，离线违规 ' + violationCount + ' 条）');
  }

  /**
   * 重新挂载后重发 ready（宿主关掉再打开面板 / 全屏⇄停靠切换后调用）。
   * ★ 页面本体一直在跑（GeoGebra 没有重建），但宿主侧的就绪闸门在重开时复位了 ——
   *   不重发 ready 的话，宿主之后发的命令会永远排队，表现成"关过一次面板之后命令全部失灵"。
   */
  function resignalReady() {
    if (!api) return;

    geoBridge().resignalReady({
      app: APP_NAME,
      perspective: PERSPECTIVE,
      href: String(global.location ? global.location.href : ''),
      title: String(global.document ? global.document.title : ''),
      ggbVersion: ggbVersion(),
      width: lastSize.w,
      height: lastSize.h,
      offlineViolations: global.OfflineGuard ? global.OfflineGuard.violationCount() : 0,
      elapsedMs: Date.now() - startedAt,
      resend: true
    });

    log('已应答宿主 hello（重发 ready）');
  }

  /** 起不来了 —— 明确告诉宿主，别让它一直等闸门。 */
  function fail(reason, detail) {
    if (readySignaled) {
      // 已经就绪过，这时再失败只当作一条错误上报，不推翻就绪状态。
      geoBridge().reportError(reason, { message: detail || '' }, 'load');
      return;
    }

    if (loadTimer) { clearTimeout(loadTimer); loadTimer = null; }

    var text = detail ? (reason + '：' + detail) : reason;
    log('载入失败：' + text);
    setVeil('failed', 'GeoGebra 未能载入', text);
    geoBridge().signalLoadFailed(reason, detail);
  }

  /** 把 api 收进内部状态；从任一回调路径进来都走这里。 */
  function adopt(got) {
    if (!got || typeof got !== 'object') return false;

    // 防呆：某些版本回调传的是 applet 容器元素而不是 API 对象。
    if (typeof got.evalCommand !== 'function' && typeof got.getVersion !== 'function') return false;

    if (api === got) return true;   // 幂等：多条回调路径都会走到这儿

    api = got;
    log('拿到 GeoGebra API（版本 ' + ggbVersion() + '）');
    signalReady();
    return true;
  }

  /** 图形变化上报。宿主侧按 ≥200ms 合并，这里不自己节流（合并策略只有一处）。 */
  var updateHooked = false;
  function hookUpdateListener() {
    if (updateHooked || !api) return;
    if (typeof api.registerUpdateListener !== 'function') return;

    updateHooked = true;

    try {
      api.registerUpdateListener(function (label) {
        geoBridge().post('changed', { label: label ? String(label) : '' });
      });
      log('已挂上图形变化监听');
    } catch (e) {
      updateHooked = false;
      geoBridge().reportError('挂接图形变化监听', e);
    }
  }

  /**
   * 发布冒烟自测（仅 ?selftest=1 时调用）：画一个圆 + 一个自由点，
   * 再通过桥回报"建出了几个对象"。这样"断网可画圆/拖点"有一条可断言的日志证据，
   * 而不是只能靠人看截图。
   */
  function runSelfTest() {
    try {
      api.evalCommand('A=(2,2)');
      api.evalCommand('B=(5,3)');
      api.evalCommand('c=Circle(A,B)');
      api.evalCommand('P=(3,4)');

      var n = (typeof api.getObjectNumber === 'function') ? api.getObjectNumber() : -1;
      var names = (typeof api.getAllObjectNames === 'function') ? String(api.getAllObjectNames()) : '';

      // ★ objects 必须当字符串发：宿主侧用 ReadPayloadString 读，数字会被读成 null 变成 "?"。
      geoBridge().post('selfTest', { objects: String(n), names: names });
      log('自测：已创建 ' + n + ' 个对象（' + names + '）');
    } catch (e) {
      geoBridge().reportError('自测画圆/画点', e);
    }
  }

  // -------------------------------------------------------- 宿主命令的处理

  function loadMaterial(base64) {
    if (!api || !base64) return;
    try {
      api.setBase64(String(base64));
      log('已载入素材');
    } catch (e) {
      geoBridge().reportError('载入素材', e);
    }
  }

  function evalCommand(command) {
    if (!api) return 'ERR:not-ready';
    if (!command) return 'ERR:empty';

    // ★ S2 自测探针：故意经 reportError 走一遍"JS → 宿主 error"通道，
    //   证明宿主日志里能看到页面侧报错（而非静默吞掉）。
    if (command === '::s2ErrorProbe::') {
      geoBridge().reportError('S2 错误通道自测', { message: 'synthetic page-side error' });
      return 'ERR:probe';
    }

    try {
      api.evalCommand(String(command));

      // ★ S2 自测：每执行成功一条命令，把当前对象清单回传宿主（写进日志），
      //   供 smoke_m75s2.py 断言"宿主命令确实在真 GeoGebra 里建了点"。
      //   ★ 用 log()（selftest 下必然回传诊断）而非 geoBridge().post，
      //      且把"读对象数 / 读对象名"各自独立 try —— 否则 GeoGebra 某个读回方法
      //      一抛异常就被内层 catch 静默吞掉，结果"命令成功但诊断没发出"（本次就踩到）。
      if (isSelfTest) {
        var _info = 'S2 selfTest command=' + String(command);
        try { if (typeof api.getObjectNumber === 'function') _info += ' count=' + api.getObjectNumber(); }
        catch (e) { _info += ' countErr=' + ((e && e.message) ? e.message : '?'); }
        try { if (typeof api.getAllObjectNames === 'function') _info += ' objects=' + String(api.getAllObjectNames()); }
        catch (e) { _info += ' namesErr=' + ((e && e.message) ? e.message : '?'); }
        log(_info);
      }

      return 'OK';
    } catch (e) {
      geoBridge().reportError('执行命令 ' + command, e);
      return 'ERR:' + ((e && e.message) ? e.message : String(e));
    }
  }

  function reset() {
    if (!api) return;

    try {
      if (typeof api.reset === 'function') api.reset();
      else if (typeof api.newConstruction === 'function') api.newConstruction();
      log('已重置工作区');
    } catch (e) {
      geoBridge().reportError('重置工作区', e);
    }
  }

  /**
   * 导出 PNG。★ 两种 API 形态都兜住：
   *   - 新版 getPNGBase64(callback, scale, transparent)
   *   - 老版 getPNGBase64() 直接返回字符串
   * 判断依据是「调用后是否同步拿到了字符串」，不去猜版本号。
   */
  function exportPng() {
    if (!api || typeof api.getPNGBase64 !== 'function') {
      geoBridge().post('exportPng', { dataUrl: '', error: '当前 GeoGebra 版本不支持导出 PNG。' });
      return;
    }

    function deliver(dataUrl) {
      geoBridge().post('exportPng', { dataUrl: dataUrl ? String(dataUrl) : '' });
    }

    try {
      var sync = api.getPNGBase64(deliver, 2, false);
      if (typeof sync === 'string' && sync.length > 0) deliver(sync);
    } catch (e) {
      geoBridge().reportError('导出 PNG', e);
      geoBridge().post('exportPng', { dataUrl: '', error: (e && e.message) ? e.message : String(e) });
    }
  }

  function onHostMessage(message) {
    if (!message || typeof message !== 'object') return;

    switch (message.type) {
      case 'loadMaterial':
        loadMaterial(message.payload && message.payload.ggbBase64);
        break;
      case 'command':
        evalCommand(message.payload && message.payload.command);
        break;
      case 'reset':
        reset();
        break;
      case 'requestExportPng':
        exportPng();
        break;
      case 'hello':
        // 宿主在重新挂载（关闭再打开 / 形态切换）后打招呼：重发 ready 放行它的闸门。
        resignalReady();
        break;
      default:
        // 宿主发来不认识的消息不是错误（可能是新版本宿主的特性），但要有痕迹。
        log('收到未识别的宿主消息：' + message.type);
        break;
    }
  }

  // -------------------------------------------------------------- 引导启动

  var booted = false;

  function boot() {
    if (booted) return;
    booted = true;

    var article = byId('ggb');
    var renderer = global.renderGGBElement;

    if (typeof renderer !== 'function') {
      fail('GeoGebra 装载器未就绪', 'renderGGBElement 不是函数 —— 页面可能没加载到 web3d/web3d.nocache.js。');
      return;
    }

    if (!article) {
      fail('页面结构异常', '找不到 id=ggb 的容器元素。');
      return;
    }

    log('装载器已就绪，开始渲染 applet');

    // 两条回调路径都挂上（不同小版本 GeoGebra 走的不一样）：
    //   ① renderGGBElement 的第二参数 appletOnLoad(api)
    //   ② 全局 ggbAppletOnLoad(api)（部分版本用这个而不是第二参数）
    global.ggbAppletOnLoad = function (api) {
      log('★ ggbAppletOnLoad 被调用（typeof=' + (api ? typeof api : 'null') + '）');
      adopt(api);
    };

    // ★ 渲染之前先把容器的真实尺寸写进参数里，这样**第一帧就是对的**。
    //   否则会先按 HTML 里写死的 1280×720 画出来，再被 fit() 拽成全屏 ——
    //   用户能看见那一跳，投影到大屏上尤其明显。
    var stage = byId('stage');
    if (stage && stage.clientWidth > 0) {
      lastSize = { w: stage.clientWidth | 0, h: stage.clientHeight | 0 };
      article.setAttribute('data-param-width', String(lastSize.w));
      article.setAttribute('data-param-height', String(lastSize.h));
      log('预设尺寸 ' + lastSize.w + '×' + lastSize.h);
    }

    try {
      // ★ renderGGBElement(容器, appletOnLoad 回调) —— 参数走容器的 data-param-* 属性。
      log('调用 renderGGBElement（article=' + (article ? article.id : 'null') + '）');
      renderer(article, function (ggbApi) {
        log('onLoad 回调收到：typeof=' + (ggbApi ? typeof ggbApi : 'null')
            + ' evalCommand=' + (ggbApi && typeof ggbApi.evalCommand)
            + ' getVersion=' + (ggbApi && typeof ggbApi.getVersion));
        if (adopt(ggbApi)) return;

        // 回调给的如果是容器元素，再从两条备用路径取 API。
        adopt(global.ggbApplet);
      });
      log('renderGGBElement 已返回（同步部分）；等待 onLoad 回调');
    } catch (e) {
      fail('渲染 GeoGebra 失败', (e && e.message) ? e.message : String(e));
      return;
    }

    // 回调是异步的，所以两条备用路径也要挂上（谁先到用谁）。
    if (!adopt(global.ggbApplet)) {
      global.ggbAppletOnLoad = function () {
        adopt(global.ggbApplet);
      };
    }

    // 兜底：回调全都没来（装载器 fetch 失败、脚本被拦、文件缺失…）。
    loadTimer = setTimeout(function () {
      if (api || readySignaled) return;

      var guard = global.OfflineGuard;
      var hint = '';

      if (guard && guard.violationCount() > 0) {
        var v = guard.violations()[0];
        hint = '（离线守门人拦截了 ' + guard.violationCount() + ' 个外部请求，例如 ' + v.url + '）';
      }

      fail('GeoGebra 载入超时', '等待 ' + Math.round(LOAD_TIMEOUT_MS / 1000) + ' 秒仍未就绪' + hint);
    }, LOAD_TIMEOUT_MS);
  }

  // -------------------------------------------------- 暴露给宿主（ExecuteScriptAsync）

  global.GeoGebraPanel = {
    isReady: function () { return !!api; },
    version: function () { return ggbVersion(); },
    objectCount: function () {
      try { return api ? api.getObjectNumber() : -1; } catch (e) { return -1; }
    },
    names: function () {
      try { return api ? String(api.getAllObjectNames()) : ''; } catch (e) { return ''; }
    },
    evalCommand: evalCommand,
    reset: reset,
    exportPng: exportPng,
    lastError: function () {
      try { return api ? String(api.getXML()) : ''; } catch (e) { return ''; }
    },
    offlineViolations: function () {
      var guard = global.OfflineGuard;
      return json(guard ? guard.violations() : []);
    },
    logs: function () { return logs.join('\n'); },
    dump: function () {
      return json({
        ready: !!api,
        readySignaled: readySignaled,
        version: ggbVersion(),
        objects: this.objectCount(),
        names: this.names(),
        size: lastSize,
        offlineViolations: global.OfflineGuard ? global.OfflineGuard.violationCount() : -1,
        elapsedMs: Date.now() - startedAt
      });
    }
  };

  // -------------------------------------------------------------- 启动序列

  var bridge = global.GeoGebraBridge;

  if (bridge) {
    bridge.start({ onMessage: onHostMessage });
  }

  global.addEventListener('resize', fit);

  // ★ 守门人必须在任何网络请求之前装好：它排在 GeoGebra 的装载器之前。
  if (global.OfflineGuard) {
    global.OfflineGuard.install({
      onViolation: function (entry) {
        // 违规一律上报：这意味着面板在试图联网，而教室里的机器没有网。
        geoBridge().reportError('面板试图访问外部地址（离线守门人已拦截）',
          { message: entry.kind + ' ' + entry.url }, 'offline');
      }
    });
    log('离线守门人已启用（本域名 ' + global.OfflineGuard.host() + '）');
  }

  if (!bridge || !bridge.isAvailable()) {
    // 用普通浏览器打开时走到这里：不是错误，只是没有宿主，行为降级为纯演示。
    log('没有宿主通道（独立打开），行为降级为纯演示');
  }

  setVeil('loading', '正在载入 GeoGebra', '本地离线载入，无需联网');
  startLoadingTicker();

  if (typeof global.renderGGBElement === 'function') {
    // ★ 竞态兜底：GWT 可能已经在我们定义 renderGGBElementReady 之前就走完了，
    //   此时它调了个不存在的函数（什么也没发生）。补一次。
    log('装载器已先行就绪，直接引导');
    boot();
  } else {
    /**
     * ★ GWT 装载器在这里回调：它已经导出 renderGGBElement，可以渲染了。
     *   定义在 panel.js（而不是行内脚本）只是为了让它可被单元化与复用。
     */
    global.renderGGBElementReady = function () {
      log('收到装载器回调 renderGGBElementReady');
      boot();
    };
  }

  log('面板脚本已就位，等待装载器');

  // ★★ 诊断探针（仅用于发布冒烟排障，不影响正常流程）：
  //   若 8 秒内装载器既没回调 renderGGBElementReady、也没把 renderGGBElement 暴露出来，
  //   说明 <strong>.cache.js 没被执行 —— 最常见原因是 WebView2 虚拟域名映射把
  //   ".cache.js" 当成未知 MIME 拒绝执行（浏览器对脚本 MIME 是强校验的）。
  //   把 renderGGBElement 的类型回传宿主，一眼就能定位是「脚本没加载」还是「回调没触发」。
  setTimeout(function () {
    if (booted) return;
    var t = (typeof global.renderGGBElement);
    geoBridge().reportError('GeoGebra 装载器未在预期时间内回调',
      { message: 'renderGGBElement 类型=' + t + '；booted=' + booted });
  }, 8000);
})(window);
