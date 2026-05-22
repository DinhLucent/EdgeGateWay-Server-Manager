Đánh giá tổng thể: **hướng thiết kế này đúng, thực tế, dễ mở rộng và khá “senior”** cho bài toán kết nối máy xét nghiệm. Nếu triển khai đúng discipline, kiến trúc này có thể handle tốt đa số analyzer hiện nay: máy chỉ đẩy kết quả 1 chiều, máy query order 2 chiều, máy ASTM dị, HL7 qua TCP/MLLP, serial RS232, mất mạng LIS/Mirth, resend kết quả, clear order, replay lỗi, audit raw message.

Tôi sẽ đánh giá theo các tiêu chí bạn hỏi.

---

## 1. Mức độ thuận tiện

**Tốt.** Điểm mạnh nhất là bạn tách rõ:

```text
Device Runtime
  Session Actor
  Protocol Policy
  Journal
  Optional Order Reply Engine
  Output Outbox
```

Thiết kế này thuận tiện vì kỹ sư vận hành không cần hiểu toàn bộ hệ thống để xử lý một máy cụ thể. Khi gặp máy mới, họ chủ yếu chỉnh:

```text
Device Communication Profile
Order query parser
Order reply template
ACK/NAK/session policy
Mapping test code
Unknown query policy
```

Core runtime vẫn giữ nguyên.

Điểm này rất quan trọng trong lĩnh vực LIS/analyzer vì máy xét nghiệm thường không “chuẩn” theo kiểu phần mềm enterprise. Cùng ASTM nhưng mỗi hãng, mỗi model, thậm chí mỗi firmware có thể lệch nhau ở field, frame, delimiter, encoding, checksum, query format, hoặc handshake.

Thiết kế theo profile/template là hướng đúng.

---

## 2. Dễ phát triển không?

**Dễ phát triển nếu bạn giữ core thật mỏng và không để business logic lọt vào actor.**

Một thiết kế tốt nên chia module như sau:

```text
core-runtime
  device-session
  transport
  protocol-boundary
  journal
  outbox
  retry
  observability

profiles
  sysmex-ca270
  sysmex-ca400
  sysmex-ca800
  mindray-bs
  cobas
  abbott
  generic-astm
  generic-hl7

order
  order-cache
  order-import
  order-matcher
  order-reply-renderer

ops
  replay
  dead-letter
  diagnostics
  profile-validator
```

Điểm cần giữ nghiêm:

**Device Actor không parse sâu business.**
Actor chỉ nên làm việc với session, frame, ACK/NAK, timeout, lifecycle. Sau khi nhận frame/message thì đẩy qua protocol boundary/journal.

**Order Reply Engine không tự quyết định y lệnh.**
Nó chỉ lấy prepared order từ cache, match sample key, render đúng template của device profile.

**Mirth/LIS mới là nơi quyết định order.**
Điều này giúp tránh EdgeGateway trở thành một “mini LIS” khó kiểm soát.

---

## 3. Dễ mở rộng không?

**Rất dễ mở rộng**, miễn là bạn chuẩn hóa concept `Device Communication Profile`.

Tôi đề xuất profile nên có cấu trúc kiểu này:

```yaml
deviceId: ca400-01
mode: receive_only # receive_only | bidirectional_order_query | bidirectional_push_order

transport:
  type: serial
  port: COM3
  baudRate: 9600
  dataBits: 8
  stopBits: 1
  parity: none

protocol:
  type: astm
  encoding: ascii
  checksum: astm_lrc
  frame:
    maxLength: 240
    terminator: CRLF

session:
  ackMode: per_frame
  timeoutMs: 15000
  retryLimit: 3
  enqCollisionPolicy: device_wins
  eotPolicy: required

result:
  parser: astm_result_v1
  outputRoute: mirth-main
  duplicatePolicy: idempotent_by_message_hash

order:
  enabled: false
  queryParser: astm_q_record_v1
  sampleKeyField: specimen_id
  replyTemplate: ca400_order_reply_v1
  unknownQueryPolicy: empty_order
  clearPolicy: clear_on_result_accepted
  orderExpiryMinutes: 1440
```

Khi muốn thêm máy mới, chỉ thêm profile/parser/template. Không sửa runtime.

Đây là tiêu chí rất quan trọng. Nếu sau này thêm Cobas, Mindray, Sysmex, Beckman, Abbott, Roche mà phải sửa core thì kiến trúc sẽ nhanh chóng hỏng. Còn nếu chỉ thêm profile thì hệ thống scale tốt.

---

## 4. Tùy cơ ứng biến với nhiều loại thiết bị

**Có thể đáp ứng tốt**, vì bạn đã nhận ra một điểm rất quan trọng: **đa số máy chỉ gửi kết quả 1 chiều, order query là tùy chọn.**

Đây là quyết định đúng. Không nên mặc định mọi máy đều 2 chiều.

Bạn nên phân loại device mode rõ:

```text
receive_only
  Máy chỉ gửi kết quả về gateway.

query_order
  Máy gửi query, gateway trả order từ local cache.

push_order
  Gateway chủ động gửi order xuống máy nếu máy hỗ trợ.

hybrid
  Vừa nhận result, vừa query/push order tùy workflow.

file_drop
  Máy đọc/ghi file qua folder/shared path.

manual_import
  Kỹ thuật viên import file kết quả.
```

Các máy xét nghiệm hiện nay rất đa dạng. Có máy dùng serial ASTM cổ điển, có máy HL7 TCP, có máy chỉ export CSV, có máy qua shared folder, có máy phải dùng middleware vendor. Kiến trúc của bạn đủ mở nếu transport/protocol/session/order tách riêng.

---

## 5. Handle mất kết nối máy chủ LIS/Mirth

**Thiết kế hiện tại handle tốt**, nhờ `Journal` và `Durable Outbox`.

Luồng đúng là:

```text
Analyzer -> Gateway -> save raw first -> queue output -> retry to Mirth/LIS
```

Không nên để việc Mirth/LIS down ảnh hưởng đến việc ACK cho analyzer, trừ khi profile bắt buộc. Với đa số thiết bị, gateway nên nhận message, journal local, ACK theo protocol, rồi forward async.

Khi Mirth down:

```text
Raw message vẫn được lưu
Packet metadata vẫn được lưu
Output outbox chuyển pending/retrying
Retry backoff
Có dead-letter nếu lỗi mapping/protocol/output kéo dài
Có replay sau khi sửa lỗi
```

Đây là thiết kế đúng production.

Tôi đề xuất thêm các state cho output:

```text
received
journaled
queued
sending
sent
accepted_by_lis
retrying
failed
dead_letter
replayed
```

Và cần phân biệt:

```text
sent_to_mirth
accepted_by_mirth
accepted_by_lis
```

Vì gửi TCP/MLLP thành công chưa chắc LIS đã xử lý thành công về mặt nghiệp vụ.

---

## 6. Gửi 2 chiều dễ thông nhau không?

**Có, nhưng chỉ nếu actor là single writer/read owner như bạn nêu.**

Đây là điểm rất quan trọng. Với ASTM/RS232, nếu RX/TX nhiều thread cùng write vào serial port thì rất dễ lỗi:

```text
ACK bị trễ
NAK sai thời điểm
EOT bị gửi nhầm
ENQ collision
Frame sequence number sai
Order reply chen vào result session
```

Bạn nói “một actor sở hữu read/write duy nhất” là chính xác.

Tôi đề xuất rule:

```text
Một physical connection = một Device Session Actor
Actor là nơi duy nhất được quyền read/write transport
Mọi module khác muốn gửi gì xuống máy phải gửi command/event cho actor
Actor serialize tất cả I/O theo session state machine
```

Ví dụ:

```text
OrderEngine không write trực tiếp vào serial/TCP.
OrderEngine chỉ build OrderReplyCommand.
Actor quyết định gửi khi protocol/session cho phép.
```

Như vậy sẽ tránh race condition.

---

## 7. Clear order khi đã có kết quả

Thiết kế hiện tại có đề cập `sent_to_device`, nhưng nên làm rõ hơn phần **clear policy**.

Một order không nên bị clear ngay khi gửi xuống máy, vì có nhiều tình huống:

```text
Máy query order xong nhưng không chạy mẫu
Máy nhận order nhưng operator hủy
Máy resend query
Máy chạy ra kết quả lỗi
Mirth/LIS chưa nhận được result
Kết quả bị reject do mapping
Gateway restart giữa chừng
```

Tôi đề xuất order lifecycle như sau:

```text
prepared
matched
reserved_for_device
sending
sent_to_device
device_acknowledged
result_received
result_forwarded
result_accepted_by_lis
completed
expired
cancelled
dead_letter
```

Và clear policy nên cấu hình theo profile/workflow:

```yaml
order:
  clearPolicy: result_accepted_by_lis
```

Các option nên có:

```text
clear_on_sent_to_device
clear_on_device_ack
clear_on_result_received
clear_on_result_forwarded
clear_on_result_accepted_by_lis
manual_clear_only
expire_after_ttl
```

Khuyến nghị production: **clear_on_result_accepted_by_lis** hoặc ít nhất **clear_on_result_forwarded**. Không nên clear ngay sau `sent_to_device`, trừ khi máy/vendor workflow bắt buộc.

---

## 8. Tính dễ sửa, dễ tinh chỉnh khi thay đổi

**Khá tốt**, với điều kiện mọi khác biệt của máy nằm trong profile/template, không nằm trong code core.

Những thứ nên chỉnh bằng config/profile:

```text
Delimiter ASTM
Encoding
Checksum
ACK/NAK mode
Timeout
Retry count
ENQ collision policy
Sample key extraction
Result duplicate key
Order matching rule
Order reply template
Unknown query behavior
Test code mapping
Output route
Patient info masking
Clear order policy
```

Những thứ nên tránh hard-code:

```text
Model CA270/CA400/CA800
ASTM field index cố định trong core
Test code mapping trong source code
Patient/order decision trong actor
LIS DB query trực tiếp khi máy đang chờ
ACK policy chung cho mọi máy
```

Bạn đã nêu đúng: **không hard-code CA270/CA400/CA800 vào core**. Đây là một trong những quyết định quan trọng nhất.

---

## 9. Những kịch bản senior nên tính thêm

Thiết kế của bạn đã tốt, nhưng để production-grade hơn, tôi sẽ bổ sung các tình huống sau.

### A. Duplicate result / resend từ máy

Máy có thể gửi lại cùng một kết quả sau timeout hoặc sau khi operator bấm resend.

Cần có idempotency key:

```text
deviceId + rawHash
deviceId + sampleId + testCode + resultTime
deviceId + messageControlId
deviceId + ASTM frame/message composite key
```

Policy:

```text
duplicate_ignore
duplicate_update
duplicate_forward_with_flag
duplicate_send_to_dead_letter
```

### B. Partial frame / corrupt frame / checksum fail

Nên journal cả lỗi protocol, không chỉ message hợp lệ.

```text
rx_bytes
frame_boundary_detected
checksum_failed
parse_failed
nak_sent
session_reset
```

Điều này rất hữu ích khi debug với máy thật.

### C. Clock lệch giữa máy, gateway, LIS

Không nên tin hoàn toàn timestamp từ analyzer.

Lưu ít nhất:

```text
device_result_time
gateway_received_time
gateway_forwarded_time
lis_accepted_time
```

### D. Mapping test code thay đổi

Test code mapping nên versioned:

```text
mappingVersion
effectiveFrom
effectiveTo
deviceCode
lisCode
```

Khi replay result cũ, phải biết dùng mapping version nào.

### E. Máy query order khi cache chưa sync

Unknown query policy nên rõ:

```text
empty_order
nak
log_only
retry_hint_if_protocol_supports
hold_session_until_timeout
```

Thông thường không nên block lâu để gọi LIS realtime, vì dễ timeout analyzer.

### F. Restart gateway giữa session

Cần durable journal/outbox, nhưng session state thì thường không recover trọn vẹn được. Sau restart nên:

```text
close/reopen port
reset protocol state
mark in-flight transaction as interrupted
allow device resend
deduplicate nếu nhận lại
```

### G. Một sample nhiều order / nhiều tube / nhiều rack

Order matching không nên chỉ assume một `sampleKey` là đủ. Nên hỗ trợ:

```text
sampleId
barcode
rackId
position
specimenType
deviceId
department
priority/stat
testCodes
```

### H. Cancel order

Nếu LIS hủy order sau khi đã sync xuống cache:

```text
prepared -> cancelled
sent_to_device -> cancellation_pending hoặc cannot_cancel_after_sent
```

Không phải máy nào cũng hỗ trợ cancel xuống device. Cần profile flag:

```yaml
order:
  supportsCancel: false
```

### I. Security và PHI/PII

Bạn đã đúng khi đề xuất UI mặc định chỉ hiện metadata/hash. Nên thêm:

```text
raw payload encrypted at rest
role-based access to raw payload
audit log khi ai xem raw
retention policy
redaction policy
```

### J. Replay an toàn

Replay phải phân biệt:

```text
replay_to_mirth
reparse_with_new_mapping
resend_order_to_device
rebuild_output_only
```

Không nên có một nút “replay” mơ hồ.

---

## 10. Điểm có thể cải thiện trong sơ đồ hiện tại

Sơ đồ hiện tại tốt, nhưng tôi sẽ thêm 4 thành phần:

```text
Profile Registry
Mapping/Template Registry
Observability/Diagnostics
Dead Letter + Replay Console
```

Sơ đồ refined:

```text
Analyzer
  <-->
Device Session Actor
  ->
Protocol Boundary
  ->
Raw/Packet Journal
  ->
Parser/Normalizer
  ->
Result Outbox
  ->
Mirth/LIS

LIS/Mirth/Order API
  ->
Order Import Worker
  ->
Local Order Cache
  ->
Order Reply Engine
  ->
Device Session Actor

Profile Registry
  -> Session Actor
  -> Protocol Boundary
  -> Order Engine
  -> Parser/Normalizer

Dead Letter + Replay Console
  -> Journal
  -> Outbox
  -> Order Cache
```

Thêm `Parser/Normalizer` giữa Journal và Output cũng hợp lý. Vì journal raw nên là dữ liệu gốc bất biến; parser/output có thể version và replay lại.

---

## 11. Rủi ro lớn nhất của thiết kế này

Rủi ro không nằm ở kiến trúc, mà nằm ở **kỷ luật triển khai**.

Các lỗi thường gặp:

```text
Cho OrderEngine query trực tiếp LIS DB khi analyzer đang chờ
Cho nhiều service cùng write vào serial port
Nhét logic riêng của từng máy vào core
Clear order quá sớm
Không journal raw trước khi parse
Không có idempotency nên resend tạo duplicate result
Không version mapping/template
UI show raw PHI mặc định
Không phân biệt transport ACK và business ACK
Không có replay/dead-letter rõ ràng
```

Nếu tránh được các lỗi này, hệ thống sẽ rất bền.

---

## 12. Kết luận

**Tôi đánh giá thiết kế này là đúng hướng, practical, dễ phát triển, dễ mở rộng, và phù hợp với thực tế máy xét nghiệm.**

Điểm mạnh nhất:

```text
EdgeGateway không quyết định y lệnh
Order là optional module
Local order cache tránh timeout analyzer
Device profile hóa mọi khác biệt
Single actor sở hữu transport I/O
Durable journal/outbox chống mất dữ liệu
Không hard-code model máy vào core
```

Tôi sẽ chốt lại thành nguyên tắc thiết kế production:

```text
Core provides mechanisms.
Profiles define device behavior.
LIS/Mirth decides medical/business intent.
Gateway guarantees transport/session correctness, durability, observability, and safe replay.
```

Nếu bạn giữ được ranh giới này, sau này gặp máy “dị”, đổi workflow, đổi Mirth, đổi LIS, thêm order 2 chiều, hoặc sửa template ASTM/HL7, bạn sẽ chủ yếu sửa profile/config/template thay vì đập lại core. Đây là hướng thiết kế đáng build.
