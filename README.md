# 🌀 SPIN GATE

> 回転するゲートの隙間にナイフを通し、輝くコアクリスタルを砕け。

回転する3Dゲートの一瞬の隙間を狙ってナイフを投げ、中央のコアクリスタルにヒットさせるワンタップ・タイミングアーケードゲームです。コアを十分に砕くとシャッターし、ゲートはより速く・より狭く再生していきます。Unity で開発し、WebGL ビルドを GitHub Pages 上でブラウザから直接プレイできます。

![Unity](https://img.shields.io/badge/Unity-6000.0.77f1-black?style=flat-square&logo=unity)
![WebGL](https://img.shields.io/badge/WebGL-Build-990000?style=flat-square)
![C#](https://img.shields.io/badge/C%23-Source-239120?style=flat-square&logo=csharp)

🔗 **[Live Demo](https://masafykun.github.io/spin-gate/)**

---

## 📸 スクリーンショット
![screenshot](screenshot.png)

---

## 🎮 操作方法
| 操作 | 動作 |
|---|---|
| タップ / クリック | ナイフを投げる |
| Space / ↑ / W | ナイフを投げる |

唯一の操作は「投げる（THROW）」のみ。ゲートの隙間がコアの前を通過する瞬間を狙ってタイミングよく投げます。

---

## ✨ 特徴
- **ワンタップ操作** — 投げるだけのシンプルな1ボタン設計
- **回転ゲート** — ゲートの隙間を通してコアを攻撃するタイミングアクション
- **シャッター演出** — コアを砕くとゲートが再生し、より速く・狭く・難しくなる
- **コンボ＆スコア** — 連続ヒットでコンボ、ベストスコアは PlayerPrefs に保存
- **ボスステージ** — ステージ進行に応じてボスゲートが出現

---

## 🛠️ 技術スタック
| カテゴリ | 技術 |
|---|---|
| ゲームエンジン | Unity (6000.0.77f1) |
| 言語 | C# |
| 配信ビルド | WebGL |
| ホスティング | GitHub Pages |

C# ソースコードは `src/` に、WebGL ビルドは `Build/` に格納されています。

---

## 🚀 セットアップ
```bash
# このリポジトリは WebGL ビルドを同梱しています。
# ローカルで動かす場合は、簡易 HTTP サーバー経由で index.html を開いてください
# （file:// 直開きでは WebGL が動作しません）。
python3 -m http.server 8000
# ブラウザで http://localhost:8000/ を開く
```

ソースから再ビルドする場合は、`src/` 内の C# スクリプトを Unity (6000.0.77f1) プロジェクトに取り込んでください。

---

## ライセンス

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)

このプロジェクトは **MIT ライセンス** のもとで公開しています。

© 2026 masafykun (https://github.com/masafykun)
