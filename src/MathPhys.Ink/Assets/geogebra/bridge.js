/*
 * bridge.js —— 面板页与宿主之间的消息封装（M7.5）
 *
 * 约定（与 C# 侧 GeoGebraProtocol 一一对应）：
 *   信封恒为 JSON 对象，带 type 字段：{ "type": "ready", "payload": { ... } }
 *   type 一律小驼峰英文，payload 里可以放中文。
 *
 * ★ 一条硬规则：**任何异常都必须回一条 error 消息**，不许静默失败。
 *   静默失败在这一头表现为「点了没反应」，而宿主日志里什么都没有 ——
 *   现场无从排查，是这个项目吃过最多亏的一类问题。
 *
 * ★★ S1 起 ready 的语义被收紧了（这是有意为之，别改回去）：
 *   S0 里 ready 在脚本一开始执行时就发，只表示「页面跑起来了」。
 *   但宿主收到 ready 就会**放行排队中的命令** —— 而此刻 GeoGebra 应用
 *   可能还要好几秒才建好，命令会静默丢失。
 *   所以现在 ready 由 panel.js 在**应用真正可用之后**显式发（signalReady），
 *   bridge 自己不再自动发。语义：**通道通 + 应用就绪 = 可以发命令了**。
 */
(function (global) {
  'use strict';

  var webview = (global.chrome && global.chrome.webview) ? global.chrome.webview : null;
  var handlers = [];
  var sentCount = 0;
  var readySent = false;

  /** 给宿主发一条消息。返回是否发出去（通道不存在时返回 false，不抛）。 */
  function post(type, payload) {
    if (!webview) return false;

    try {
      var envelope = { type: type };
      if (payload !== undefined && payload !== null) envelope.payload = payload;
      webview.postMessage(envelope);
      sentCount++;
      return true;
    } catch (e) {
      // 这里不能调 reportError —— 连发送都失败了，再发一条 error 也是失败。
      if (global.console && console.error) console.error('bridge.post 失败', e);
      return false;
    }
  }

  /** 把异常报给宿主（宿主会写进日志并在状态栏显示）。 */
  function reportError(where, e, kind) {
    var detail = (e && e.message) ? e.message : String(e);
    post('error', { message: where + '：' + detail, kind: kind || 'js' });
  }

  /** 分发一条来自宿主的消息；每个订阅者单独 try/catch，一个坏了不影响其它。 */
  function dispatch(raw) {
    var message = raw;

    if (typeof message === 'string') {
      try {
        message = JSON.parse(message);
      } catch (e) {
        reportError('解析宿主消息', e);
        return;
      }
    }

    for (var i = 0; i < handlers.length; i++) {
      try {
        handlers[i](message);
      } catch (e) {
        reportError('处理消息 ' + (message && message.type ? message.type : '(未知)'), e);
      }
    }
  }

  /**
   * 启动桥接：只负责「挂监听」。
   *
   * ★ 不在这里发 ready —— 见文件头关于 ready 语义的说明。
   *   页面真正就绪时由业务代码显式调 signalReady()。
   *   加载**失败**时调 signalLoadFailed()，否则宿主会一直等闸门放行（表现为「点了没反应」）。
   *
   * @param {{onMessage?: function}} options
   * @returns {boolean} 通道是否可用
   */
  function start(options) {
    options = options || {};

    if (typeof options.onMessage === 'function') handlers.push(options.onMessage);

    if (!webview) {
      // 用普通浏览器打开这个文件时会走到这里 —— 不是错误，只是没有宿主。
      if (global.console && console.warn) console.warn('bridge：没有 chrome.webview，独立打开时属正常。');
      return false;
    }

    webview.addEventListener('message', function (event) { dispatch(event.data); });

    // 页面级未捕获异常也回宿主：否则表现是一块动也不动的白板。
    global.addEventListener('error', function (event) {
      reportError('页面脚本错误', event.error || event.message);
    });
    global.addEventListener('unhandledrejection', function (event) {
      reportError('未处理的 Promise 拒绝', event.reason);
    });

    return true;
  }

  /**
   * 显式发出 ready（就绪闸门的放行信号）。**只发一次**。
   *
   * @param {object} [info] 页面自报的信息。宿主会把它写进日志 ——
   *   这一条把「JS 那一半到底通没通」从「看屏幕猜」变成「查日志即知」：
   *   页面白屏、脚本没执行、加载到了错的页面，这三种情况都骗不过它。
   *   ★ 建议至少带上 href / title / app / ggbVersion。
   */
  function signalReady(info) {
    if (readySent) return false;
    readySent = true;

    var payload = { bridge: true };
    if (info) {
      for (var key in info) {
        if (Object.prototype.hasOwnProperty.call(info, key)) payload[key] = info[key];
      }
    }

    return post('ready', payload);
  }

  /**
   * 重新挂载后重发 ready（宿主 hello 消息的应答）。
   *
   * ★★ 与 signalReady 的区别（别把两者混掉）：
   *   signalReady 一生只发一次 —— 多条回调路径都会收敛到它，
   *   重复发会让宿主把排队命令放行两遍；
   *   而宿主在「关掉再打开 / 全屏⇄停靠切换」后会主动打招呼（hello）要求重发，
   *   因为宿主侧的就绪闸门在重开时复位了 —— 此时的重发是宿主明确要的，
   *   不发的话宿主之后发的命令会永远排队（表现成"关过一次面板后命令全部失灵"）。
   */
  function resignalReady(info) {
    var payload = { bridge: true, resend: true };
    if (info) {
      for (var key in info) {
        if (Object.prototype.hasOwnProperty.call(info, key)) payload[key] = info[key];
      }
    }

    return post('ready', payload);
  }

  /**
   * 告诉宿主「应用起不来了，别再等 ready 了」。
   *
   * ★ 这条消息决定用户体验的档次：没有它，加载失败时宿主的命令会一直排队，
   *   用户看到的是「点了没反应」；有它，宿主能立刻把原因显示在状态栏上。
   *
   * @param {string} reason 一句话原因（中文，给人看的）
   * @param {string} [detail] 细节（异常消息、缺失的文件名等）
   */
  function signalLoadFailed(reason, detail) {
    post('loadFailed', { reason: String(reason || 'GeoGebra 未能载入。'), detail: detail ? String(detail) : '' });
  }

  global.GeoGebraBridge = {
    isAvailable: function () { return !!webview; },
    post: post,
    start: start,
    signalReady: signalReady,
    resignalReady: resignalReady,
    signalLoadFailed: signalLoadFailed,
    reportError: reportError,
    sentCount: function () { return sentCount; },
    readySent: function () { return readySent; }
  };
})(window);
