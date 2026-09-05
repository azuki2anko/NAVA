## 変更内容

<!-- 何を、なぜ変更したかを簡潔に記載してください。 -->

## 確認項目

- [ ] `dotnet build NetworkAVAmp.Controller.sln -c Release`が成功する
- [ ] `dotnet test NetworkAVAmp.Controller.sln -c Release`が成功する
- [ ] `dotnet format NetworkAVAmp.Controller.sln --verify-no-changes`が成功する
- [ ] `getFeatures`にない操作や値を送信しない
- [ ] 実IP、MACアドレス、機器ID、トークン、個人パスを含めていない
- [ ] `.private/`または`private/`を参照していない
- [ ] 実機の状態変更テストを行った場合、対象操作と確認結果を記載した

## 実機確認

<!-- 未実施／RX-V4Aで読取のみ／RX-V4Aで操作確認、など。ほかの機種を確認済みと推測で記載しないでください。 -->
