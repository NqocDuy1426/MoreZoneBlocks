# More Zone Blocks (Cities: Skylines)

**More Zone Blocks** là mod mở rộng giới hạn khối quy hoạch (Zone Blocks) từ trần gốc 49.152 lên **65.535 blocks (+33.3% dung lượng)** cho *Cities: Skylines*.

Được phát triển và tối ưu hóa bởi **NgocDuy**.

---

## 🌟 Tính Năng Nổi Bật

1. **Mở Rộng Trần 16-Bit An Toàn (65.535 Blocks):**
   - Mở rộng `ZoneManager.m_blocks` từ 49.152 lên 65.536 phần tử.
   - Bảo toàn 100% kiểu dữ liệu `ushort` trong `NetSegment`, `ZoneBlock` và `ZoneManager.m_zoneGrid`. Không làm tràn bộ nhớ thanh ghi CPU.

2. **Kiến Trúc Two-Stream Serialization (Bảo Toàn Savegame 100%):**
   - **Save Sanitization:** Trước khi lưu, các con trỏ block $\ge 49.152$ trong `NetSegment` được tạm thời mask về `0`, lưu bảng ánh xạ sang Sidecar Stream (`MoreZoneBlocks_Extended`).
   - Mở file save trên game không có mod (Vanilla) **hoàn toàn không bị crash** `IndexOutOfRangeException`.
   - Khi nạp lại với mod, toàn bộ các block $\ge 49.152$ và con trỏ đường được phục hồi hoàn chỉnh.

3. **Điều Chỉnh Chu Kỳ Mô Phỏng (SimulationStepImpl Pacing):**
   - Transpiler sửa bước nhảy từ 48 lên 64 block mỗi frame ($1.024 \times 64 = 65.536$).
   - Đảm bảo 100% các block mở rộng đều được tick mô phỏng và **nhà mọc bình thường**.

4. **Bọc Thép 18 Vị Trí Inlined `49152` (Loop Guard Transpilers):**
   - Triệt tiêu lỗi `Invalid list detected!` trên tất cả các hàm duyệt danh sách liên kết (`ZoneBlock`, `Building.CheckZoning`, `ZoneTool`, `DefaultTool`, `BuildingTool`).

5. **Auto-Garbage Collector An Toàn Luồng (Simulation-Thread Safe):**
   - Quét nền định kỳ trên Simulation Thread (16 segment/tick) để giải phóng các Zone Block rác, rỗng không có nhà trên cao tốc và đường ray tàu.
   - Tuyệt đối không gây race condition trên `Array16<T>.ReleaseItem()`.

6. **Tương Thích Tuyệt Đối Với 81 Tiles 2:**
   - Transpiler vi phẫu độc lập, không can thiệp vào `ZONEGRID_RESOLUTION` ($270 \times 270$) của mod 81 Tiles 2.

---

## 🛠 Hướng Dẫn Biên Dịch & Kiểm Thử

```bash
cd /Users/nqocduy/CS_MoreZoneBlocks
./build.sh
```

Kịch bản `build.sh` sẽ tự động:
1. Biên dịch `MoreZoneBlocks.dll` bằng `mcs`.
2. Chạy toàn bộ 48 bài kiểm thử giả lập (Simulation Test Suite) kiểm tra tính toàn vẹn bộ nhớ, serialization và IL transpilers.
3. Cài đặt tự động vào thư mục mod của game `Addons/Mods/MoreZoneBlocks`.
4. Chuẩn bị sẵn file cho SteamCMD tại `content/`.

---

## 📄 Bản Quyền
Phát triển bởi **NgocDuy** cho cộng đồng *Cities: Skylines*.

