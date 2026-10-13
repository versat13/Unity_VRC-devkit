---
name: vrc-devkit
description: VRChatアバター/ギミックのFX・同期・Constraintを解析するUnity拡張「VRC DevKit」の使い方。FXや同期を変更した後や実機テストの前、「リモートでずれる・同期しない・ステートが止まる」の調査、アバター構造の把握に使う。
---

# VRC DevKit（com.versat.vrc-devkit）

Unity エディタ拡張。アバターの**一時クローンに NDMF/MA を適用した最終状態**を読み取り専用で解析し、同期・Animator の典型的な誤りを検出する。元のアバターは変更しない。

## 呼び出し方
1. unityMCP で対象インスタンスを選ぶ（`set_active_instance`。ツール名は環境で変わる。例: `mcp__remote-devices__unityMCP__*` や `mcp__claude-device__lcl-unityMCP-*`）
2. MCP ツール `vrc_devkit` があればそれを使う（`action=help` から）。無ければ execute_code で静的APIを呼ぶ:
   ```csharp
   return Versat.VrcDevKit.Api.Lint("アバター名");            // options JSON は第2引数
   return Versat.VrcDevKit.Api.Dump("アバター名", "{\"sections\":\"params,layers\",\"layerFilter\":\"PS_\"}");
   return Versat.VrcDevKit.Api.ReadReport("lint", "md", 20000);
   ```
   すべて JSON 文字列を返す（`ok` が false なら `error` を読む）。
3. `vrc_devkit` が一覧に無いときは、MCP の再接続かツール一覧の更新で現れることがある。execute_code で十分なので待たない。

| action / Api | 用途 |
|---|---|
| status | 導入機能、シーン内アバター、除外・抑制の状況 |
| rules | ルール一覧と意味 |
| lint(avatar, options) | 検出。要約を返し、全文は `DevKitReports/<日時>_lint_<avatar>/` と `lint_latest.*` に保存 |
| dump(avatar, options) | 構造の書き出し（同期パラメータとbit、レイヤー/ステート/遷移、Constraint、Contact、PhysBone）。`layerFilter`/`pathFilter`(正規表現)と `sections` で絞る |
| read_report(target, format, maxChars) | 保存済みレポートを読む（`lint`/`dump`/フォルダ/ファイル） |
| suppress / unsuppress | 意図的な所見を id 単位で抑制（理由必須） |
| mark_owner_only_layer(pattern, reason) | 効果が所有者にしか見えないレイヤーを宣言（R001/R002 を抑制） |
| exclude_avatar | DevKit が絶対に触らないアバターを登録 |
| select(avatar, path) | 人間向けに元アバターの該当オブジェクトを選択 |
| self_test | 全ルールの回帰テスト（ルール変更後に必ず実行） |

## 標準ワークフロー
1. `status` → 対象アバター名と除外を確認
2. `lint` → 所見を1件ずつ判断（下表）
3. 修正はユーザーの方針どおり（方針を提示し、許可を得てから変更）。DevKit 自体は何も修正しない
4. 再度 `lint`。意図的なものだけ理由付きで `suppress` / `mark_owner_only_layer`
5. 構造が分からないときは `dump` を絞って読む。Unity の UI を開かせない

## ルールと判断
| ID | 検出 | 本物の不具合の目安 | 誤検出になりやすい例と対処 |
|---|---|---|---|
| R001 | IsLocal で分岐されていない経路が、ローカルでしか書かれないパラメータ（非同期のメニュー項目、localOnly Driver、ローカル専用ステートの Driver）を読む | そのレイヤーが他人に見える物（本体の見た目、音、ワールドに置く物の位置）を動かしている | 所有者専用のカメラ・ファインダー・UI だけを動かすレイヤー → 何を動かしているか確かめてから `mark_owner_only_layer`（理由に根拠を書く） |
| R002 | PhysBone `_IsGrabbed/_IsPosed`、localOnly Contact の値をリモート経路で読む | リモートの見た目・状態がその値で決まる | 所有者の UI だけ → 同上 |
| R003 | 条件も ExitTime もない遷移（永久に止まる）、条件なしの AnyState 遷移 | ほぼ常に不具合 | 意図的なことはまれ |
| R004 | 同じ Transform の位置/回転/スケールを複数レイヤーが別の軸で書く | 後のレイヤーが全軸を上書きする | — 1レイヤーの 2D BT にまとめる |
| R005 | 1つの BlendTree 内に同名の別クリップ | 生成コードで取り違える | 手作業で問題が出ていなければ info 扱い |
| R006 | Driver の float→int（VRChat は切り捨て） | 量子化が1ずれる | 変換先を [min+0.5, max+0.5] にすると四捨五入 |

レイヤーが何を動かしているかは、execute_code で `AvatarSession.Open` → `BuildContext` → `Analyze` → `LayerInfo` の各ステートのクリップを `AnimationUtility.GetCurveBindings` で列挙すると分かる。

## 安全上の決まり
- `excludedAvatars` のアバターは解析もクローンもしない。除外を外さない
- suppress と mark には必ず理由を書き、ユーザーへの報告に含める
- `ProjectSettings/VrcDevKitSettings.json`（除外・抑制・所有者専用レイヤー）はバージョン管理対象
- 解析は一時クローンで行う。Play 中は実行中のアバターをそのまま読む

## Unity 内では分からないこと（実機で確認）
送信タイミングとジッター、リモート IK、クライアントをまたぐ Contact/PhysBone のグラブ、Station 着席時の不具合、遠距離のカリング、FPS に依存する積分のずれ。

## 同期設計の原則（Lint で拾えない手戻りを減らす）
- リモートは**同期パラメータだけから決定論的に再構成**できる形にする
- 物理（PhysBone/Contact）の**過程ではなく結果**を、オーナー側で同期パラメータへコピーする
- float→int の Driver Copy は変換先を +0.5 ずらす
- 巡回送信（Order/Val 方式）は送信間隔が悪化したときの取りこぼしを想定する

## ルールを足すとき
`Editor/Avatars/Rules.cs` に `ILintRule` を追加し、`RuleRegistry.All` に登録する。`SelfTest.cs` に「検出する」と「検出しない」の両方のケースを足し、`self_test` が全件合格することを確認する。所見の `Id` は安定させる（レイヤー名・パラメータ名などから組み立てる）。
