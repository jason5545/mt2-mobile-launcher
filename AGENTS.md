# 給 AI 代理的規則

這個 repo 之後會公開。下面幾條沒有例外。

## 絕對不能進版控

- 任何遊戲檔案：`*.dll`、`*_Data/`、`*.assets`、`*.bundle`、`*.resS`、音檔、圖片素材、原版 exe 抽出的圖示。
- 反編譯的 C#（AssetRipper 的 `ExportedProject/`、`decompiled/`）。
- 轉換產物：產生的 TS／JS（`gen/`）、匯入器輸出（`public/game/` 這類）、轉好的 shader、patch 過的 DLL、patcher 從原版 DLL 產生的替身（`Stubs.g.cs`）。
- APK、AAB、keystore、`local.properties`。
- token、密碼、SteamID、帳號名稱，以及主機名稱、內網位址、手機型號、本機絕對路徑。

不確定某個檔案算不算遊戲內容，就當它算，不 commit，列在回報裡。

## 手寫的程式

- 相容層可以寫原版的類別名稱、方法名稱、數值，不可以貼反編譯出來的程式碼片段。
- 從 unity-port 搬程式過來時，逐檔看過再搬，不要整個資料夾複製。unity-port 的 git 歷史不帶過來。
- `src/UnityNative`、`src/Patcher`、`src/Shared/RawTexture.cs` 的正本在 unity-port（`unity-native/engine`、`unity-native/patcher`、`unity-native/Shared`），兩邊逐檔相同。unity-port 是維護者私有的開發 repo，不公開（裡面還有別的遊戲的原型與參考資料）。
  - 維護者：改引擎先改 unity-port，再用 `tools/sync-engine.sh` 同步過來，照上一條逐檔看過 `git diff` 再 commit；`tools/sync-engine.sh --check` 確認兩邊相同。
  - 沒有 unity-port 的人：照常在這裡改、開 PR。維護者合併前把同樣的改動做進 unity-port，同步回來之後 `--check` 要回 0。
- AssetRipper 的修改只放在 `third_party/AssetRipper/assetripper-local.patch`（GPL-3.0）。`third_party/AssetRipper/build/src` 是 `fetch.sh` 抓的上游，不 commit。要改 AssetRipper，就在 `build/src` 改，`git -C third_party/AssetRipper/build/src add -A`、`git -C third_party/AssetRipper/build/src diff --cached > third_party/AssetRipper/assetripper-local.patch` 重產 patch，再跑一次 `fetch.sh` 確認套得上；`THIRD_PARTY_NOTICES.md` 的修改清單跟著改。

## 參考資料（維護者）

- 原版遊戲與反編譯碼在維護者本機的 `~/GitHub/unity-port/mt2-web/reference/`，不在任何 repo 的版控裡；只讀，不改。
- 測試需要遊戲檔時，讀 `reference/` 的路徑或放到 repo 外的暫存資料夾，不要複製進這個 repo。

## 同步（維護者）

Mac 與開發機都是 `~/GitHub/mt2-mobile-launcher`，用 git 同步。

## 正本與公開 repo（維護者）

- 正本是維護者私有的 repo（完整歷史，含 `docs/` 開發紀錄），Mac 與開發機用它同步。
- 公開的 `jason5545/mt2-mobile-launcher` 是正本的快照：不帶歷史、不含 `docs/`，用 `tools/publish-public.sh "<訊息>"` 產生，看過再 `--push`。`docs/` 不公開，README 與其他公開的檔案不要連到 `docs/`。
- 外部的 PR 開在公開 repo。維護者把同樣的改動做進正本，再發一次快照；不在公開 repo 直接合併（下一次快照會蓋掉）。
- Release 的原始碼包（`tools/release/package.sh`）照同樣的範圍，不含 `docs/`。

## 裝 APK 到手機

代理用 adb 覆蓋安裝啟動器之後，照這個順序，不要跳步：

1. 裝 APK（`adb install -r`）。中途斷線（`device offline`）時，手機上的安裝可能還在跑：先重新連上，看 `dumpsys package org.mt2mobile.launcher` 的 `lastUpdateTime` 跟 `pm list sessions`（或等一分鐘），確定沒有進行中的安裝才重裝。10/28 斷線後馬上重裝，兩次撞在一起，系統把舊版整個移除再全新安裝，遊戲、Steam 登入、手機上的存檔都清掉了。
2. 非官方建置（各機器 debug keystore 簽的，`RELEASE=1` 以外的 `build.sh`）要先同意建置簽章：啟動器一打開會自動問（APK 更新過才會），核對指紋後按「我了解風險，繼續」。官方簽章的（`RELEASE=1`，指紋 `ea3c5c9d…`）不會問，這步跳過。
3. 等遊戲頁的 Steam Cloud 卡片變成「已同步」。
4. 這時才啟動遊戲。

非官方建置的同意記到下一次安裝或更新為止，每裝一次都要重新同意。沒同意就開遊戲，遊戲拿不到 Steam 身分與 DLC，開著 DLC 開始的那一局會跳「此遊戲是在已解除安裝的 DLC 下開始的」，不能繼續（game.log 會寫「Steam 身分快取未就緒」）。看到這個對話框只按「確定」、返回，不要開新遊戲；關掉遊戲、回啟動器同意、等同步再開。10/24 踩到兩次（細節在 `docs/phase4.md`「風險提示與同意」）。
