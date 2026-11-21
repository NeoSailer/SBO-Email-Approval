using System;
using System.Linq;
using System.Collections.Generic;
using SAPbouiCOM;

namespace PVI.Logistics
{
    public sealed class Viewer
    {
        public const string Uid = "PVI_APR_VIEW";
        public const string ResultUid = "PVI_APR_RESULT";
        public const string PreviewUid = "PVI_APR_DEV";
        public const string PreviewResultUid = "PVI_APR_DEV_R";
        static readonly string[] StatusCodes = { "W", "Y", "N", "P", "A", "C" };
        static readonly string[] TypeCodes = { "540000006", "22" };
        readonly SAPbouiCOM.Application app;
        readonly string criteriaUid, resultUid;
        Form form;
        bool resultReady;
        bool layingOut;
        string fontKey;
        ReportLayout layout;
        List<ApprovalReportRow> reportRows = new List<ApprovalReportRow>();
        readonly HashSet<string> expanded = new HashSet<string>();
        readonly Dictionary<string, string> payloads = new Dictionary<string, string>();
        readonly Dictionary<string, string> stageNames = new Dictionary<string, string>();
        readonly Dictionary<string, List<string[]>> stageCache = new Dictionary<string, List<string[]>>();
        string selectedHash;
        static readonly string[] ResultColumns = ApprovalReport.Columns.Concat(new[] { "DocEntry", "ObjType", "TemplateKey", "Hash", "ApprovalKey", "RowIndex" }).ToArray();
        bool rendering;
        bool detailVisible;
        public Viewer(SAPbouiCOM.Application app, Store store) : this(app, store, false) { }
        public Viewer(SAPbouiCOM.Application app, Store store, bool preview)
        { this.app = app; criteriaUid = preview ? PreviewUid : Uid; resultUid = preview ? PreviewResultUid : ResultUid; }
        public bool Owns(string uid) { return uid == criteriaUid || uid == resultUid; }
        public bool IsLinkEvent(ItemEvent e)
        { return e.FormUID == resultUid && e.BeforeAction && e.EventType == BoEventTypes.et_MATRIX_LINK_PRESSED &&
            ((e.ItemUID == "grid" && new[] { "DocNum", "DraftNum", "TemplateName", "DraftKey" }.Contains(e.ColUID)) || (e.ItemUID == "stages" && e.ColUID == "StageName")); }
        // 부모/자식 행이 서로 떨어지지 않도록 SAP의 개별 열 정렬은 사용하지 않는다.
        public bool BlocksSort(ItemEvent e) { return e.FormUID == resultUid && e.BeforeAction && e.EventType == BoEventTypes.et_GRID_SORT && e.ItemUID == "grid"; }
        public void OpenLink(int visualRow, string column, string item = "grid")
        {
            var result = app.Forms.Item(resultUid);
            try {
                var grid = (Grid)result.Items.Item(item).Specific;
                if (visualRow < 0 || grid.DataTable.IsEmpty) return;
                int row = grid.GetDataTableRowIndex(visualRow); if (row < 0) return;
                string keyField = item == "stages" ? "StageKey" : column == "DocNum" ? "DocEntry" : column == "TemplateName" ? "TemplateKey" : "DraftKey";
                string key = Convert.ToString(grid.DataTable.GetValue(keyField, row));
                int objectType = item == "stages" ? 120 : column == "DocNum" ? Snapshot.Key(Convert.ToString(grid.DataTable.GetValue("ObjType", row))) : column == "TemplateName" ? 121 : 112;
                if (Snapshot.Key(key) == 0 || objectType == 0) { app.StatusBar.SetText("이 기록에는 연결할 문서 또는 설정 키가 없습니다.", BoMessageTime.bmt_Short, BoStatusBarMessageType.smt_Warning); return; }
                // 표시 번호(DocNum)가 아니라 보존한 내부 키(DocEntry/WtmCode)로 이동한다.
                var opened = app.OpenForm((BoFormObjectEnum)objectType, "", key);
                Store.Release(opened);
            } catch (System.Runtime.InteropServices.COMException ex) {
                throw new InvalidOperationException("연결 대상을 열 수 없습니다. 원본 삭제 여부와 SAP 권한을 확인하세요. 저장된 이력은 유지됩니다. " + ex.Message);
            } finally { Store.Release(result); }
        }
        void BindCheck(string id)
        {
            form.DataSources.UserDataSources.Add(id, BoDataType.dt_SHORT_TEXT, 1);
            var check = (CheckBox)form.Items.Item(id).Specific;
            check.ValOn = "Y"; check.ValOff = "N"; check.DataBind.SetBound(true, "", id);
            form.DataSources.UserDataSources.Item(id).ValueEx = "Y";
        }
        void BindUser(string id)
        {
            // SAP 사용자 개체(12)의 기본 선택 목록을 연결하고 사용자 코드를 입력란에 표시한다.
            form.DataSources.UserDataSources.Add(id, BoDataType.dt_SHORT_TEXT, 100);
            var edit = (EditText)form.Items.Item(id).Specific;
            edit.DataBind.SetBound(true, "", id);
            var cp = (ChooseFromListCreationParams)app.CreateObject(BoCreatableObjectType.cot_ChooseFromListCreationParams);
            try {
                cp.UniqueID = "cfl_" + id; cp.ObjectType = "12"; cp.MultiSelection = false;
                var cfl = form.ChooseFromLists.Add(cp); Store.Release(cfl);
                edit.ChooseFromListUID = cp.UniqueID;
                edit.ChooseFromListAlias = "USER_CODE";
            } finally { Store.Release(cp); }
        }
        public void Show(bool loadResults = true)
        {
            try { form = app.Forms.Item(criteriaUid); form.Visible = true; RefreshTypography(); form.Select(); HideResults(); return; } catch { }
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "forms", "ApprovalHistory.srf");
            var document = new System.Xml.XmlDocument(); document.Load(path);
            var root = document.SelectSingleNode("/Application/forms/action/form");
            if (root == null || root.Attributes["uid"].Value != Uid) throw new InvalidOperationException("조회 화면 SRF의 UID가 올바르지 않습니다.");
            root.Attributes["uid"].Value = criteriaUid;
            string xml = document.OuterXml;
            app.LoadBatchActions(ref xml);
            try { form = app.Forms.Item(criteriaUid); }
            catch { throw new InvalidOperationException("SRF 화면 생성 실패: " + app.GetLastBatchResults()); }
            bool ready = false;
            form.Freeze(true);
            try {
                BindUser("requester"); BindUser("approver");
                for (int i = 0; i < 6; i++) BindCheck("s" + i);
                for (int i = 0; i < 2; i++) BindCheck("t" + i);
                ApplyCriteriaLayout();
                ready = true;
            } finally { form.Freeze(false); if (!ready) form.Close(); }
            form.Visible = true;
            HideResults();
        }
        void HideResults()
        { try { var r = app.Forms.Item(resultUid); try { r.Visible = false; } finally { Store.Release(r); } } catch { } }
        void ApplyCriteriaLayout()
        {
            layout = ReportLayout.Measure(app.FontName, app.FontHeight);
            form.ClientWidth = layout.Width; form.ClientHeight = layout.Height;
            foreach (var pair in layout.Items) {
                var item = form.Items.Item(pair.Key);
                item.Left = pair.Value.X; item.Top = pair.Value.Y;
                item.Width = pair.Value.Width; item.Height = pair.Value.Height;
            }
            foreach (string id in new[] { "lst", "ltype", "lreq", "lapp" }) form.Items.Item(id).TextStyle = (int)BoTextStyle.ts_BOLD;
            fontKey = app.FontName + ":" + app.FontHeight;
            KeepOnScreen(form);
        }
        void KeepOnScreen(Form target)
        {
            var desktop = app.Desktop;
            try {
                var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                int width = Math.Min(desktop.Width, screen.Width), height = Math.Min(desktop.Height, screen.Height);
                if (target.Left < 0 || target.Left + target.Width > width) target.Left = Math.Max(0, width - target.Width - 10);
                if (target.Top < 0 || target.Top + target.Height > height) target.Top = Math.Max(0, height - target.Height - 10);
            } finally { Store.Release(desktop); }
        }
        // SAP 글꼴 설정이 바뀌면 글자 측정값으로 폼과 항목을 다시 배치한다.
        public void RefreshTypography()
        {
            if (layingOut) return;
            if (fontKey == app.FontName + ":" + app.FontHeight) {
                foreach (string id in new[] { criteriaUid, resultUid }) {
                    try { var open = app.Forms.Item(id); try { if (open.Visible) KeepOnScreen(open); } finally { Store.Release(open); } } catch { }
                }
                return;
            }
            layingOut = true;
            try {
                try { form = app.Forms.Item(criteriaUid); form.Freeze(true); try { ApplyCriteriaLayout(); } finally { form.Freeze(false); } } catch { }
                if (resultReady) ResizeResults();
            } finally { layingOut = false; }
        }
        public void Handle(ref ItemEvent e)
        {
            if (e.BeforeAction) return;
            if (e.EventType == BoEventTypes.et_FORM_ACTIVATE) RefreshTypography();
            if (e.FormUID == criteriaUid) {
                if (e.EventType == BoEventTypes.et_CHOOSE_FROM_LIST) {
                    // 선택 이벤트 안에서는 EditText.Value 대신 바인딩된 데이터 소스를 변경해야 한다.
                    var choice = (IChooseFromListEvent)e;
                    var selected = choice.SelectedObjects;
                    if (selected != null && selected.Rows.Count > 0 && (e.ItemUID == "requester" || e.ItemUID == "approver"))
                        form.DataSources.UserDataSources.Item(e.ItemUID).ValueEx = Convert.ToString(selected.GetValue("USER_CODE", 0));
                }
                if (e.EventType == BoEventTypes.et_ITEM_PRESSED && e.ItemUID == "ok") LoadResults();
                return;
            }
            if (e.FormUID != resultUid) return;
            if (e.EventType == BoEventTypes.et_ITEM_PRESSED && e.ItemUID == "criteria") Show();
            if (e.EventType == BoEventTypes.et_ITEM_PRESSED && (e.ItemUID == "expand" || e.ItemUID == "collapse")) {
                expanded.Clear();
                if (e.ItemUID == "expand") foreach (var row in reportRows.Where(r => r.IsDocument)) expanded.Add(row.GroupKey);
                var result = app.Forms.Item(resultUid);
                try { RenderRows(result); ClearDetail(result); } finally { Store.Release(result); }
            }
            if (e.EventType == BoEventTypes.et_FORM_RESIZE && resultReady && !layingOut) ResizeResults();
            if (!rendering && e.EventType == BoEventTypes.et_CLICK && e.ItemUID == "grid" && e.Row >= 0) SelectReportRow(e.Row, e.ColUID);
        }
        public Filters ReadFilters()
        {
            form = app.Forms.Item(criteriaUid);
            var f = new Filters {
                Requester = ((EditText)form.Items.Item("requester").Specific).Value.Trim(),
                Approver = ((EditText)form.Items.Item("approver").Specific).Value.Trim()
            };
            for (int i = 0; i < 6; i++) if (((CheckBox)form.Items.Item("s" + i).Specific).Checked) f.Statuses.Add(StatusCodes[i]);
            for (int i = 0; i < 2; i++) if (((CheckBox)form.Items.Item("t" + i).Specific).Checked) f.Types.Add(TypeCodes[i]);
            return f;
        }
        public void LoadResults()
        {
            // 확인 버튼을 누른 시점의 조건으로 최신 저장 이력을 별도 읽기 전용 결과 창에 표시한다.
            string where = ReadFilters().Where();
            // 없는 UDT를 직접 조회하면 SAP가 원인 대신 300-33을 반환하므로 먼저 구조를 확인한다.
            DataTable metadata;
            try { metadata = form.DataSources.DataTables.Item("schema"); }
            catch { metadata = form.DataSources.DataTables.Add("schema"); }
            metadata.ExecuteQuery("SELECT \"TableName\" FROM \"OUTB\" WHERE \"TableName\" IN ('PVI_ARVC','PVI_APVH')");
            if (metadata.IsEmpty || metadata.Rows.Count != 2)
                throw new InvalidOperationException("조회할 승인 이력 테이블이 없습니다. PVI_ARVC / PVI_APVH를 확인하세요. 현재 애드온은 조회 전용으로 테이블을 자동 생성하지 않습니다.");
            metadata.ExecuteQuery("SELECT \"TableID\", \"AliasID\" FROM \"CUFD\" WHERE \"TableID\" IN ('@PVI_ARVC','@PVI_APVH')");
            var fields = new System.Collections.Generic.HashSet<string>();
            if (!metadata.IsEmpty) for (int i = 0; i < metadata.Rows.Count; i++) fields.Add(Convert.ToString(metadata.GetValue(0, i)) + ":" + Convert.ToString(metadata.GetValue(1, i)));
            if (new[] { "@PVI_ARVC", "@PVI_APVH" }.Any(t => Store.Fields.Keys.Any(f => !fields.Contains(t + ":" + f))))
                throw new InvalidOperationException("승인 이력 테이블에 필요한 필드가 없습니다. 관리자에게 기존 테이블 구조를 확인해 주세요. 조회 전용 애드온은 필드를 생성하거나 변경하지 않습니다.");
            Form result;
            bool created = false;
            try { result = app.Forms.Item(resultUid); }
            catch {
                resultReady = false;
                created = true;
                var cp = (FormCreationParams)app.CreateObject(BoCreatableObjectType.cot_FormCreationParams);
                try { cp.UniqueID = resultUid; cp.FormType = "PVI_APR_RESULTS"; cp.BorderStyle = BoFormBorderStyle.fbs_Sizable; result = app.Forms.AddEx(cp); }
                finally { Store.Release(cp); }
                result.Title = "승인 이력 조회# - 결과";
                // 열이 늘어도 첫 화면에서 최대한 읽을 수 있도록 SAP 작업 영역 너비를 활용한다.
                var desktop = app.Desktop;
                try {
                    result.Left = 10; result.Top = 10;
                    // 일부 SAP/Windows 배율 조합의 Desktop은 물리 픽셀을 반환하므로 논리 화면 크기로 제한한다.
                    var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                    result.ClientWidth = Math.Max(600, Math.Min(desktop.Width, screen.Width) - 60);
                    result.ClientHeight = Math.Max(440, Math.Min(650, Math.Min(desktop.Height, screen.Height) - 100));
                } finally { Store.Release(desktop); }
                // 요약 문구 없이 표를 맨 위에 배치한다. 폰트는 모든 열이 SAP 기본 설정을 상속한다.
                Add(result, "grid", BoFormItemTypes.it_GRID, 10, 10, 865, 300);
                Add(result, "stages", BoFormItemTypes.it_GRID, 10, 320, 865, 180);
                ((Button)Add(result, "criteria", BoFormItemTypes.it_BUTTON, 15, 395, 100, 25).Specific).Caption = "선택 기준";
                ((Button)Add(result, "2", BoFormItemTypes.it_BUTTON, 125, 395, 85, 25).Specific).Caption = "닫기";
                ((Button)Add(result, "collapse", BoFormItemTypes.it_BUTTON, 235, 395, 85, 25).Specific).Caption = "접기";
                ((Button)Add(result, "expand", BoFormItemTypes.it_BUTTON, 345, 395, 85, 25).Specific).Caption = "펼치기";
                result.DataSources.DataTables.Add("source"); result.DataSources.DataTables.Add("detail");
                var rows = result.DataSources.DataTables.Add("results");
                foreach (string id in ResultColumns) rows.Columns.Add(id, BoFieldsType.ft_AlphaNumeric, 254);
                var stages = result.DataSources.DataTables.Add("stages");
                foreach (string id in ApprovalReport.StageColumns) stages.Columns.Add(id, BoFieldsType.ft_AlphaNumeric, 254);
                ((Grid)result.Items.Item("grid").Specific).DataTable = rows;
                ((Grid)result.Items.Item("stages").Specific).DataTable = stages;
            }
            result.Freeze(true);
            try {
                var dt = result.DataSources.DataTables.Item("source");
                dt.ExecuteQuery(ReportQuery.Select + where + " ORDER BY \"U_Captured\" DESC, \"U_WddCode\"");
                var values = new List<Dictionary<string, object>>();
                var sourceColumns = new string[dt.Columns.Count];
                for (int c = 0; c < sourceColumns.Length; c++) sourceColumns[c] = dt.Columns.Item(c).Name;
                if (!dt.IsEmpty) for (int r = 0; r < dt.Rows.Count; r++) {
                    var row = new Dictionary<string, object>();
                    for (int c = 0; c < sourceColumns.Length; c++) row[sourceColumns[c]] = dt.GetValue(c, r);
                    values.Add(row);
                }
                reportRows = ApprovalReport.Group(values); expanded.Clear(); payloads.Clear(); stageNames.Clear(); stageCache.Clear(); selectedHash = null;
                // 과거 XML에 단계명이 없는 경우에만 현재 설정의 이름으로 보완한다. 새 스냅샷은 이름 자체를 보존한다.
                var lookup = result.DataSources.DataTables.Item("detail");
                lookup.ExecuteQuery("SELECT \"WstCode\", \"Name\" FROM \"OWST\"");
                if (!lookup.IsEmpty) for (int r = 0; r < lookup.Rows.Count; r++) stageNames[Convert.ToString(lookup.GetValue(0, r))] = Convert.ToString(lookup.GetValue(1, r));
                // 선택마다 DB에 왕복하지 않도록 조회된 요청의 불변 이력을 한 번에 읽어 캐시한다.
                var hashes = reportRows.Where(r => !r.IsDocument).Select(r => r.Text("Hash")).Where(h => h != "").Distinct().ToArray();
                for (int offset = 0; offset < hashes.Length; offset += 100) {
                    lookup.ExecuteQuery("SELECT \"Code\", \"U_Payload\" FROM \"@PVI_APVH\" WHERE \"Code\" IN (" + String.Join(",", hashes.Skip(offset).Take(100).Select(Filters.Q)) + ")");
                    if (!lookup.IsEmpty) for (int r = 0; r < lookup.Rows.Count; r++) payloads[Convert.ToString(lookup.GetValue(0, r))] = Convert.ToString(lookup.GetValue(1, r));
                }
                ConfigureGrid((Grid)result.Items.Item("grid").Specific, false);
                ConfigureGrid((Grid)result.Items.Item("stages").Specific, true);
                RenderRows(result); ClearDetail(result);
                resultReady = true;
                ResizeResults();
                result.Visible = true; result.Select();
                // 조회 성공 후에만 조건 창을 숨긴다. 오류가 나면 입력한 조건을 그대로 수정할 수 있다.
                form.Visible = false;
            } catch {
                if (created) { resultReady = false; result.Close(); }
                throw;
            } finally { try { result.Freeze(false); } catch { } Store.Release(result); }
        }
        static Item Add(Form target, string id, BoFormItemTypes type, int x, int y, int w, int h)
        { var item = target.Items.Add(id, type); item.Left=x; item.Top=y; item.Width=w; item.Height=h; return item; }
        void ResizeResults()
        {
            var result = app.Forms.Item(resultUid);
            bool previous = layingOut; layingOut = true;
            try {
                layout = ReportLayout.Measure(app.FontName, app.FontHeight);
                int m = 10, gap = 8;
                int minWidth = Math.Max(layout.Width, 4 * layout.ButtonWidth + 3 * gap + 2 * m);
                int minHeight = layout.RowHeight * 8 + layout.ButtonHeight + 4 * m;
                if (result.ClientWidth < minWidth) result.ClientWidth = minWidth;
                if (result.ClientHeight < minHeight) result.ClientHeight = minHeight;
                int w = result.ClientWidth - 2 * m;
                int y = result.ClientHeight - m - layout.ButtonHeight;
                int area = y - gap - m;
                int upperHeight = (area - gap) * 3 / 5;
                var grid = result.Items.Item("grid"); grid.Left = m; grid.Top = m; grid.Width = w; grid.Height = detailVisible ? upperHeight : area;
                var stages = result.Items.Item("stages"); stages.Left = m; stages.Top = m + upperHeight + gap; stages.Width = w; stages.Height = area - upperHeight - gap;
                stages.Visible = detailVisible;
                string[] buttons = { "criteria", "2", "collapse", "expand" };
                for (int i = 0; i < buttons.Length; i++) {
                    var button = result.Items.Item(buttons[i]);
                    button.Left = i < 2 ? m + i * (layout.ButtonWidth + gap) : result.ClientWidth - m - (4 - i) * layout.ButtonWidth - (3 - i) * gap;
                    button.Top = y; button.Width = layout.ButtonWidth; button.Height = layout.ButtonHeight;
                }
                FitColumns((Grid)grid.Specific, false, w);
                FitColumns((Grid)stages.Specific, true, w);
                KeepOnScreen(result);
            } finally { layingOut = previous; Store.Release(result); }
        }
        // 긴 비고 하나 때문에 모든 열이 밀리지 않도록 헤더 최소 폭과 가중치로 폭을 배분한다.
        // 폰트를 축소하지 않는다. 매우 큰 글꼴/작은 화면에서 최소 폭이 부족하면 SAP 가로 스크롤을 유지한다.
        void FitColumns(Grid grid, bool detail, int width)
        {
            string[] captions = detail ? ApprovalReport.StageCaptions : ReportQuery.Captions.Concat(new[] { "문서 총액(LC)" }).ToArray();
            if (!detail && reportRows.Count > 0) {
                // 들여쓰기/요청 번호가 추가되어도 문서 이름이 잘리지 않도록 첫 열의 최소 폭을 확보한다.
                captions[0] = reportRows.Select(r => r.Caption(true)).OrderByDescending(s => s.Length).First();
            }
            int[] weights = detail ? new[] { 17, 14, 8, 12, 11, 38 } : new[] { 8, 6, 7, 7, 12, 7, 10, 5, 7, 8, 8, 15, 11 };
            var sizes = ReportLayout.ColumnWidths(app.FontName, app.FontHeight, width - 35, captions, weights);
            for (int i = 0; i < captions.Length; i++) grid.Columns.Item(i).Width = sizes[i];
        }
        void ConfigureGrid(Grid grid, bool detail)
        {
            var captions = detail ? ApprovalReport.StageCaptions : ReportQuery.Captions.Concat(new[] { "문서 총액(LC)" }).ToArray();
            for (int i = 0; i < grid.Columns.Count; i++) {
                var column = grid.Columns.Item(i);
                column.Editable = false; column.FontSize = -1; column.TextStyle = (int)BoTextStyle.ts_PLAIN_FONT;
                column.RightJustified = !detail && column.UniqueID == "Total";
                column.Visible = i < captions.Length;
                if (column.Visible) column.TitleObject.Caption = captions[i];
            }
            grid.SelectionMode = BoMatrixSelect.ms_Single;
            if (detail) ((EditTextColumn)grid.Columns.Item("StageName")).LinkedObjectType = "120";
            else {
                ((EditTextColumn)grid.Columns.Item("DocNum")).LinkedObjectType = "22";
                ((EditTextColumn)grid.Columns.Item("DraftNum")).LinkedObjectType = "112";
                ((EditTextColumn)grid.Columns.Item("DraftKey")).LinkedObjectType = "112";
                ((EditTextColumn)grid.Columns.Item("TemplateName")).LinkedObjectType = "121";
            }
        }
        void RenderRows(Form result)
        {
            rendering = true; result.Freeze(true);
            try {
                var data = result.DataSources.DataTables.Item("results"); data.Rows.Clear();
                var visible = reportRows.Select((r, i) => new { Row = r, Index = i }).Where(r => r.Row.IsDocument || expanded.Contains(r.Row.GroupKey)).ToList();
                if (visible.Count > 0) data.Rows.Add(visible.Count);
                for (int r = 0; r < visible.Count; r++) {
                    var row = visible[r].Row;
                    for (int c = 0; c < ResultColumns.Length; c++) {
                        string id = ResultColumns[c];
                        string value = id == "RowIndex" ? visible[r].Index.ToString() : row.Text(id);
                        if (id == "DocType") value = row.Caption(expanded.Contains(row.GroupKey));
                        data.SetValue(id, r, value);
                    }
                }
            } finally { result.Freeze(false); rendering = false; }
        }
        void ClearDetail(Form result)
        {
            // 요청을 선택하기 전에는 빈 상세 표가 목록 영역을 차지하지 않는다.
            selectedHash = null;
            if (!detailVisible) return;
            detailVisible = false; result.Items.Item("stages").Visible = false;
            result.DataSources.DataTables.Item("stages").Rows.Clear();
            if (resultReady) ResizeResults();
        }
        void SelectReportRow(int visualRow, string column)
        {
            var result = app.Forms.Item(resultUid);
            try {
                var grid = (Grid)result.Items.Item("grid").Specific;
                int row = grid.GetDataTableRowIndex(visualRow); if (row < 0 || grid.DataTable.IsEmpty) return;
                var selected = reportRows[Convert.ToInt32(grid.DataTable.GetValue("RowIndex", row))];
                if (selected.IsDocument) {
                    if (column == "DocType") {
                        if (!expanded.Remove(selected.GroupKey)) expanded.Add(selected.GroupKey);
                        RenderRows(result);
                    }
                    ClearDetail(result); return;
                }
                string hash = selected.Text("Hash"), payload;
                if (detailVisible && selectedHash == hash) return;
                List<string[]> steps;
                if (!stageCache.TryGetValue(hash, out steps)) {
                    payloads.TryGetValue(hash, out payload);
                    steps = ApprovalReport.Stages(payload, stageNames); stageCache[hash] = steps;
                }
                var data = result.DataSources.DataTables.Item("stages");
                result.Freeze(true);
                try {
                    data.Rows.Clear(); if (steps.Count > 0) data.Rows.Add(steps.Count);
                    for (int r = 0; r < steps.Count; r++) for (int c = 0; c < ApprovalReport.StageColumns.Length; c++) data.SetValue(c, r, steps[r][c]);
                    selectedHash = hash;
                    if (!detailVisible) { detailVisible = true; ResizeResults(); }
                } finally { result.Freeze(false); }
            } finally { Store.Release(result); }
        }
        public void Close()
        {
            foreach (string id in new[] { resultUid, criteriaUid }) {
                try { var open = app.Forms.Item(id); try { open.Close(); } finally { Store.Release(open); } } catch { }
            }
        }
    }
}
