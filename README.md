# VRC DevKit


※これは自分の環境で使い回すためにリポジトリ登録しただけのもの。

LLM（Claude + unityMCP）から操作することを前提にした、VRChat ギミック開発用の読み取り専用解析ツール。

- アバターの一時クローンに NDMF/MA を適用し、最終的な FX などを解析（元アバターは変更しない）
- すべての機能が `Versat.VrcDevKit.Api.*`（文字列を受け取り JSON を返す）と、MCP ツール `vrc_devkit` で使える
- 結果は `DevKitReports/`（プロジェクト直下、Assets 外）に JSON/Markdown で保存
- 設定（除外アバター・抑制・所有者専用レイヤー）は `ProjectSettings/VrcDevKitSettings.json`

## 導入
Package Manager → Add package from git URL:
```
https://github.com/versat13/Unity_VRC-devkit.git
```
要件: Unity 2022.3 / VRChat Avatars SDK 3.x。任意: NDMF（MA 適用後の解析）、unityMCP（MCP ツール化）

## Lint ルール
| ID | 内容 |
|---|---|
| R001 | リモートでも動く経路が、ローカルでしか変化しないパラメータを読む |
| R002 | PhysBone の IsGrabbed/IsPosed、localOnly Contact の値をリモート経路で読む |
| R003 | 条件も ExitTime もない遷移／無条件の AnyState 遷移 |
| R004 | 同じ Transform の位置/回転/スケールを複数レイヤーが別の軸で書く |
| R005 | BlendTree 内で同名の別クリップ |
| R006 | Parameter Driver の float→int 切り捨て |

`Tools > VRC DevKit` に人間用のウィンドウとセルフテストがある。

特定バージョンに固定するときは末尾に `#v0.1.0` を付ける。

## LLM 用 Skill
`Skill~/vrc-devkit/SKILL.md`（Claude 向けの使い方・判断基準）。`~` 付きフォルダは Unity に読み込まれない。

## 使い方（Claude / unityMCP）
```csharp
return Versat.VrcDevKit.Api.Help();
return Versat.VrcDevKit.Api.Lint("アバター名");
return Versat.VrcDevKit.Api.Dump("アバター名", "{\"sections\":\"params,layers\"}");
```
