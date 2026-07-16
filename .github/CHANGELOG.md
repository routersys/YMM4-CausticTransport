# v1.0.0 - 光輸送結像 for YMM4

YukkuriMovieMaker4向けの光輸送結像エフェクトプラグインの初回リリースです。
現在のフレームの明るさを輸送格子の上に光の分布として集め、選択した光源形状が定める目標分布へ移す輸送写像を求めます。
集光度に応じて写像を進め、光源形状へ広げた光を元映像へ結像させます。
計算はComputeSharpの計算シェーダーがDirect3D 12で実行し、YMM4のDirect3D 11側とは共有テクスチャおよび共有フェンスで接続します。
8言語のリソース構成のUIを備えます。

---

## 新機能

### 1. 光輸送の計算パイプライン

`CausticTransportPipeline`は、1フレーム分の計算シェーダーを1つの`ComputeContext`へ記録して実行します。輸送格子と積算バッファーは入力サイズと品質に応じて確保し、サイズが変わらないフレームでは再利用します。処理の流れは次のとおりです。

1. `GridDepositShader`が、各格子セルに対応する画素のBT.709輝度（`0.2126 R + 0.7152 G + 0.0722 B + 0.01 A`）を合計し、光の分布を作ります。
2. `LightShapeShader`が、光源形状から目標分布を作ります。面は全体を1とし、円とスリットは光源サイズが定める半径の内側を1、外側を0へ`SmoothStep`で落とします。
3. `GridRowSumShader`と`NormalizeScaleShader`が、光の分布と目標分布の総量を求め、両者を同じ総量へそろえる倍率を計算します。
4. `InitializeDisplacementShader`が、輸送写像の初期値を置きます。面は移動なし、円は正方格子を円板へ写す写像、スリットは片方の軸を帯へ圧縮する写像を初期値とします。
5. 輸送反復では、`PushforwardShader`が分布を写像に沿って押し出し、`ResidualShader`が押し出した分布と目標分布の差を求めます。差を右辺として、`RestrictShader`・`JacobiShader`・`ProlongShader`によるマルチグリッドのVサイクルでポテンシャルのポアソン方程式を解き、`UpdateDisplacementShader`がポテンシャルの勾配に沿って写像を更新します。
6. `SplatShader`が、各画素の色を集光度に応じた移動先へ加算し、`ResolveShader`が積算値を正規化して出力します。

押し出しと積算は、`Hlsl.InterlockedAdd`で加算するために固定小数点で行います。押し出しは倍率`16384`、積算は画素数から`256`〜`65536`の範囲で選ぶ倍率を使い、32ビット整数の範囲を超えないようにします。

| シェーダー | 役割 |
|---|---|
| `GridDepositShader` | 画素の輝度を格子セルへ合計する |
| `LightShapeShader` | 光源形状から目標分布を作る |
| `GridRowSumShader` / `NormalizeScaleShader` | 分布と目標分布の総量をそろえる倍率を求める |
| `InitializeDisplacementShader` | 光源形状から輸送写像の初期値を置く |
| `PushforwardShader` | 分布を写像に沿って押し出す |
| `ResidualShader` | 押し出した分布と目標分布の差を求める |
| `RestrictShader` / `ProlongShader` | マルチグリッドの粗化と補間を行う |
| `JacobiShader` | ポアソン方程式をJacobi法で解く |
| `UpdateDisplacementShader` | ポテンシャルの勾配で写像を更新する |
| `SplatShader` | 画素の色を移動先へ加算する |
| `ResolveShader` | 積算値を正規化して出力する |

写像の更新は、緩和係数`0.7`で勾配を掛け、1回の移動量を格子`3`セル分に制限します。マルチグリッドは、長辺が`16`セル以下になる粗さまで階層を作ります。

### 2. Direct3D 11・Direct3D 12相互運用

`CausticTransportGpuInterop`は、YMM4のDirect3D 11・Direct2D側と、ComputeSharpのDirect3D 12側を接続します。ComputeSharpの`GraphicsDevice`は、YMM4が使うDXGIアダプターのLUIDと一致するものを選びます。

入力と出力は、ComputeSharpで確保した共有テクスチャをDirect3D 11のテクスチャとして開き、Direct2Dのビットマップとして扱います。入力は、Direct2Dの描画コンテキストが共有テクスチャへ`SourceCopy`で描き込みます。両デバイスの同期は、Direct3D 12のフェンスを共有フェンスとしてDirect3D 11側で開いて行います。

`BeginCompute`は、Direct3D 11のコマンドを送出したうえでDirect3D 12側を待機させ、`EndCompute`は、Direct3D 12側の完了をDirect3D 11側で待ちます。通常のフレーム処理では、CPUへの画素読み戻しとCPUからの画素再転送を行いません。

Direct3D 12デバイスの取得や共有リソースの作成に失敗した場合は、`TryCreate`が`null`を返し、エフェクトを適用せず入力映像を表示します。

### 3. カスタムシェーダーによる合成

`CausticTransportCustomEffect`は、`[CustomEffect(2)]`の2入力エフェクトです。入力0は元映像、入力1は輸送した光の像です。ピクセルシェーダー`CausticTransport.hlsl`の`main`は、`amount`が0以下のとき元映像をそのまま返し、そうでないときは光の像のRGBをアルファでクランプし、`lerp(source, caustic, amount)`で合成します。

定数バッファーは`Amount`と3つの詰め物で16バイトです。`MapInputRectsToOutputRect`は入力0の矩形をそのまま出力矩形とし、出力範囲を拡張しません。輸送は入力の範囲内に収まるため、素材より大きな出力範囲を必要としません。

シェーダーリソース: `pack://application:,,,/CausticTransport;component/Shaders/CausticTransport.cso`（ps_5_0、`ShaderResourceUri.Get`が生成）

### 4. エフェクト定義とパラメータ

`CausticTransportEffect`は、YMM4の映像エフェクトとして宣言されます。

`[VideoEffect]`属性は以下のパラメーターで宣言されます。

- 表示名: `Texts.CausticTransport`（ローカライズキー、日本語では「光輸送結像」）
- カテゴリー: `VideoEffectCategories.Filtering`・`VideoEffectCategories.Decoration`
- 検索タグ: `TagCaustic`・`TagLight`・`TagTransition`
- `IsAviUtlSupported = false`によりAviUtl向けEXO出力は非対応
- `ResourceType = typeof(Texts)`でローカライズリソースを指定

公開プロパティは以下のとおりです。基本項目は「基本」グループ、光源項目は「光源」グループ、光学項目は「光学」グループに属します。

| プロパティ | 型 | デフォルト | 内部範囲 | アニメーション |
|---|---|---|---|---|
| `Amount` | `Animation` | 100 | 0〜100 | あり |
| `Focus` | `Animation` | 50 | 0〜100 | あり |
| `Quality` | `CausticTransportQuality` | `High` | — | なし |
| `LightShape` | `CausticLightShape` | `Plane` | — | なし |
| `ApertureSize` | `Animation` | 50 | 5〜100 | あり |
| `Dispersion` | `Animation` | 30 | 0〜100 | あり |
| `Roughness` | `Animation` | 20 | 0〜100 | あり |
| `Seed` | `int` | 0 | 0〜int.MaxValue | なし |

`GetAnimatables`は`Amount`・`Focus`・`ApertureSize`・`Dispersion`・`Roughness`を返します。`Seed`は負値を代入すると0へ丸めます。

`CreateExoVideoFilters`は空のシーケンスを返します（EXO非対応）。`CreateVideoEffect`は映像処理用のインスタンスを生成します。エフェクトを最初に生成したときに、更新確認を一度だけ開始します。

### 5. フレームごとの更新

各フレームでYMM4の`EffectDescription`からフレーム位置、アイテム長、FPSを取得し、アニメーション値を評価します。値をパイプラインが前提とする範囲へ制限してから転送します。

| パラメータ | 変換 |
|---|---|
| `Amount` | `value / 100` をカスタムシェーダーの`Amount`へ |
| `Focus` | `value / 100` を0〜1へクランプ |
| `ApertureSize` | `value / 100` を0.05〜1へクランプ |
| `Dispersion` | `value / 100` を0〜1へクランプ |
| `Roughness` | `value / 100` を0〜1へクランプ |
| `LightShape` | 列挙値を整数の形状番号へ |
| `Quality` | 列挙値をそのまま |
| `Seed` | 0以上へクランプ |

強さが0以下のときは輸送を行わず、入力映像をそのまま出力します。集光度が1以上のときはカスタムシェーダーの`Amount`を0にし、入力映像をそのまま出力します。入力の範囲が有限でない場合や画素数が上限を超える場合も、入力映像を表示します。

パイプライン側では、集光度から移動量`1 − 集光度`を求め、面粗さから散乱量`面粗さ × 0.5`を求め、分散から色ごとの移動量の広がり`分散 × 0.35`を求めます。

### 6. 品質設定

品質は、輸送格子の解像度、輸送写像の反復回数、Jacobi反復回数をまとめて切り替えます。

| 品質 | 格子解像度 | 輸送反復 | Jacobi反復 |
|---|---:|---:|---:|
| 標準 | 128 | 4回 | 12回 |
| 高品質 | 192 | 6回 | 16回 |
| 最高品質 | 256 | 8回 | 20回 |

格子解像度は長辺のセル数です。短辺のセル数は入力映像の縦横比に合わせ、最小4セルとします。

### 7. ローカライズ

`Texts`クラスは`[AutoGenLocalizer]`属性を持つ`partial`クラスとして宣言されます。
`YukkuriMovieMaker.Generator`のソースジェネレーターが`Texts.csv`を処理し、各ロケールのリソースファイルを自動生成します。

対応リソース: 日本語（`ja-jp`）・英語（`en-us`）・中国語簡体字（`zh-cn`）・中国語繁体字（`zh-tw`）・韓国語（`ko-kr`）・スペイン語（`es-es`）・アラビア語（`ar-sa`）・インドネシア語（`id-id`）

主なローカライズキーは以下のとおりです。

| キー | ja-jp |
|---|---|
| `CausticTransport` | 光輸送結像 |
| `BasicGroup` | 基本 |
| `LightGroup` | 光源 |
| `OpticsGroup` | 光学 |
| `Amount` | 強さ |
| `Focus` | 集光度 |
| `Quality` | 品質 |
| `LightShape` | 光源形状 |
| `ApertureSize` | 光源サイズ |
| `Dispersion` | 分散 |
| `Roughness` | 面粗さ |
| `Seed` | シード |
| `ShapePlane` | 面 |
| `ShapeCircle` | 円 |
| `ShapeHorizontalSlit` | 横スリット |
| `ShapeVerticalSlit` | 縦スリット |
| `QualityBalanced` | 標準 |
| `QualityHigh` | 高品質 |
| `QualityUltra` | 最高品質 |
| `TagCaustic` | 集光 |
| `TagLight` | 光 |
| `TagTransition` | 遷移 |
| `UpdateAvailableMessage` | 新しいバージョン {0} が公開されています。 |
