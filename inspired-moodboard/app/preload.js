// Bridge between the board page and the app. Only these calls are available to the page.
const { contextBridge, ipcRenderer, webUtils } = require("electron");
const call = (name) => (...a) => ipcRenderer.invoke(name, ...a);
contextBridge.exposeInMainWorld("board", {
  init: call("init"), collections: call("collections"), pickFolder: call("pickFolder"), openFolder: call("openFolder"), reload: call("reload"),
  saveLayout: call("saveLayout"), setOnTop: call("setOnTop"), setOpacity: call("setOpacity"), setFloat: call("setFloat"),
  copyImage: call("copyImage"), copyImageData: call("copyImageData"), copyText: call("copyText"),
  showInFolder: call("showInFolder"), openPath: call("openPath"), openLink: call("openLink"), exportBoard: call("exportBoard"),
  startDrag: (p) => ipcRenderer.send("startDrag", p),
  pathForFile: (f) => { try { return webUtils.getPathForFile(f); } catch (_) { return ""; } },
  onFolderChanged: (fn) => ipcRenderer.on("folder-changed", (_e, root) => fn(root)),
  onOpenPath: (fn) => ipcRenderer.on("open-path", (_e, p) => fn(p))
});
