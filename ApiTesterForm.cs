using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace postmanclone
{
    // --- Environment Variables Manager ---
    public static class EnvManager
    {
        public static Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();

        public static string Replace(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;

            return Regex.Replace(input, @"\{\{(.+?)\}\}", match =>
            {
                string key = match.Groups[1].Value.Trim();
                return Variables.TryGetValue(key, out string val) ? val : match.Value;
            });
        }
    }

    // --- HttpClient Factory (Fixes DNS Staleness) ---
    public static class SimpleHttpClientFactory
    {
        private static HttpClient _client;
        private static DateTime _lastCreated = DateTime.MinValue;
        private static readonly object _lock = new object();

        public static HttpClient GetClient()
        {
            lock (_lock)
            {
                // Refresh client every 5 minutes to avoid DNS staleness without relying on DI
                if (_client == null || (DateTime.UtcNow - _lastCreated).TotalMinutes > 5)
                {
                    _client?.Dispose();
                    _client = new HttpClient();
                    _lastCreated = DateTime.UtcNow;
                }
                return _client;
            }
        }
    }

    // --- Models ---
    public class FormDataItem
    {
        public string Key { get; set; }
        public string Type { get; set; } // "Text" or "File"
        public string Value { get; set; }
    }

    public class RequestHistoryItem
    {
        public string CustomName { get; set; }
        public string Method { get; set; }
        public string Url { get; set; }
        public string AuthType { get; set; }
        public string AuthToken { get; set; }
        public string AuthUser { get; set; }
        public string AuthPass { get; set; }

        public string ApiKeyName { get; set; }
        public string ApiKeyValue { get; set; }
        public string ApiKeyAddTo { get; set; }

        // OAuth 2.0 Auth Properties
        public string OAuth2AuthUrl { get; set; }
        public string OAuth2TokenUrl { get; set; }
        public string OAuth2ClientId { get; set; }
        public string OAuth2ClientSecret { get; set; }
        public string OAuth2Scope { get; set; }
        public string OAuth2RedirectUri { get; set; }
        public string OAuth2Token { get; set; }

        public List<KeyValuePair<string, string>> Headers { get; set; } = new List<KeyValuePair<string, string>>();

        // Advanced Body Types
        public string BodyType { get; set; } = "raw"; // "raw", "form-data", "x-www-form-urlencoded"
        public string Body { get; set; } // Raw string
        public List<FormDataItem> FormData { get; set; } = new List<FormDataItem>();
        public List<KeyValuePair<string, string>> UrlEncodedData { get; set; } = new List<KeyValuePair<string, string>>();

        public DateTime Timestamp { get; set; }

        public override string ToString()
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName;
            return $"{Method}  {Url}";
        }
    }

    public class AppState
    {
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
        public List<RequestHistoryItem> History { get; set; } = new List<RequestHistoryItem>();
        public List<CollectionFolder> Collections { get; set; } = new List<CollectionFolder>();
    }

    public class CollectionFolder
    {
        public string Name { get; set; }
        public List<RequestHistoryItem> Requests { get; set; } = new List<RequestHistoryItem>();
    }

    // --- Main Form Shell ---
    public partial class ApiTesterForm : Form
    {
        private SplitContainer mainSplitter;
        private TabControl tabSidebar;
        private ListBox lstHistory;
        private TextBox txtHistorySearch;
        private List<RequestHistoryItem> allHistoryItems = new List<RequestHistoryItem>();
        private TreeView tvCollections;
        private TabControl tabRequests;
        private ToolStrip mainToolStrip;
        private TabPage tabAddButton;
        private readonly string dataFilePath = "appdata.json";

        public ApiTesterForm()
        {
            InitializeMainUI();

            LoadData();

            AddNewTab();
            tabRequests.TabPages.Add(tabAddButton);

            this.KeyPreview = true;
            this.KeyDown += ApiTesterForm_KeyDown;
            this.FormClosing += ApiTesterForm_FormClosing;
        }

        private void InitializeMainUI()
        {
            this.Text = "Lightweight API Tester (Postman Clone)";
            this.Size = new Size(1200, 800);
            this.MinimumSize = new Size(800, 500);

            // --- Top Toolbar ---
            mainToolStrip = new ToolStrip();
            ToolStripButton btnNewTab = new ToolStripButton("➕ New Tab");
            btnNewTab.Click += (s, e) => AddNewTab();

            ToolStripButton btnCloseTab = new ToolStripButton("❌ Close Tab");
            btnCloseTab.Click += (s, e) => CloseCurrentTab();

            ToolStripButton btnImportCol = new ToolStripButton("📂 Import Collection");
            btnImportCol.Click += (s, e) => ImportCollection();

            ToolStripButton btnEnv = new ToolStripButton("🌍 Environments");
            btnEnv.Click += (s, e) => ShowEnvManagerDialog();

            mainToolStrip.Items.Add(btnNewTab);
            mainToolStrip.Items.Add(new ToolStripSeparator());
            mainToolStrip.Items.Add(btnCloseTab);
            mainToolStrip.Items.Add(new ToolStripSeparator());
            mainToolStrip.Items.Add(btnImportCol);
            mainToolStrip.Items.Add(new ToolStripSeparator());
            mainToolStrip.Items.Add(btnEnv);
            this.Controls.Add(mainToolStrip);

            // --- Main Layout ---
            mainSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 250,
                FixedPanel = FixedPanel.Panel1
            };

            // --- Left Panel ---
            tabSidebar = new TabControl { Dock = DockStyle.Fill };
            TabPage pageHistory = new TabPage("History");
            TabPage pageCollections = new TabPage("Collections");

            // Setup History Search Bar
            Panel pnlHistoryTop = new Panel { Dock = DockStyle.Top, Height = 35, Padding = new Padding(5) };
            txtHistorySearch = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9),
                Text = "Search history...",
                ForeColor = Color.Gray
            };
            txtHistorySearch.GotFocus += (s, e) => {
                if (txtHistorySearch.Text == "Search history...") { txtHistorySearch.Text = ""; txtHistorySearch.ForeColor = Color.Black; }
            };
            txtHistorySearch.LostFocus += (s, e) => {
                if (string.IsNullOrWhiteSpace(txtHistorySearch.Text)) { txtHistorySearch.Text = "Search history..."; txtHistorySearch.ForeColor = Color.Gray; }
            };
            txtHistorySearch.TextChanged += (s, e) => {
                if (txtHistorySearch.Text != "Search history...") RefreshHistoryList();
            };
            pnlHistoryTop.Controls.Add(txtHistorySearch);

            // Setup Custom Drawn History ListBox
            lstHistory = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Font = new Font("Segoe UI", 9),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 24
            };
            lstHistory.DrawItem += LstHistory_DrawItem;
            lstHistory.DoubleClick += LstHistory_DoubleClick;

            ContextMenuStrip historyContextMenu = new ContextMenuStrip();
            ToolStripMenuItem renameHistoryItem = new ToolStripMenuItem("Rename");
            renameHistoryItem.Click += (s, e) => {
                if (lstHistory.SelectedItem is RequestHistoryItem item)
                {
                    string newName = PromptForInput("Rename History Item", "Enter new name:", item.CustomName ?? item.Url);
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        item.CustomName = newName;
                        lstHistory.Invalidate();
                    }
                }
            };

            ToolStripMenuItem deleteHistoryItem = new ToolStripMenuItem("Delete");
            deleteHistoryItem.Click += (s, e) => {
                if (lstHistory.SelectedIndex != -1 && lstHistory.SelectedItem is RequestHistoryItem item)
                {
                    allHistoryItems.Remove(item);
                    RefreshHistoryList();
                }
            };

            historyContextMenu.Items.Add(renameHistoryItem);
            historyContextMenu.Items.Add(deleteHistoryItem);

            lstHistory.ContextMenuStrip = historyContextMenu;
            lstHistory.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    int index = lstHistory.IndexFromPoint(e.Location);
                    if (index != ListBox.NoMatches) lstHistory.SelectedIndex = index;
                }
            };
            pageHistory.Controls.Add(lstHistory);
            pageHistory.Controls.Add(pnlHistoryTop);
            pnlHistoryTop.SendToBack(); // Dock Top
            lstHistory.BringToFront();  // Dock Fill below it

            tvCollections = new TreeView { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9), HideSelection = false };
            tvCollections.NodeMouseDoubleClick += TvCollections_NodeMouseDoubleClick;

            ContextMenuStrip collectionsContextMenu = new ContextMenuStrip();

            ToolStripMenuItem importCollectionItem = new ToolStripMenuItem("Import Collection...");
            importCollectionItem.Click += (s, e) => ImportCollection();

            ToolStripMenuItem newCollectionItem = new ToolStripMenuItem("New Collection");
            ToolStripMenuItem renameCollectionItem = new ToolStripMenuItem("Rename");
            ToolStripMenuItem deleteCollectionItem = new ToolStripMenuItem("Delete");

            newCollectionItem.Click += (s, e) => {
                string name = PromptForInput("New Collection", "Enter collection name:", "New Collection");
                if (!string.IsNullOrWhiteSpace(name)) tvCollections.Nodes.Add(new TreeNode(name));
            };

            renameCollectionItem.Click += (s, e) => {
                if (tvCollections.SelectedNode != null)
                {
                    string name = PromptForInput("Rename", "Enter new name:", tvCollections.SelectedNode.Text);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        tvCollections.SelectedNode.Text = name;
                        if (tvCollections.SelectedNode.Tag is RequestHistoryItem req) req.CustomName = name;
                    }
                }
            };

            deleteCollectionItem.Click += (s, e) => {
                if (tvCollections.SelectedNode != null) tvCollections.Nodes.Remove(tvCollections.SelectedNode);
            };

            collectionsContextMenu.Items.AddRange(new ToolStripItem[] { importCollectionItem, new ToolStripSeparator(), newCollectionItem, renameCollectionItem, deleteCollectionItem });
            tvCollections.ContextMenuStrip = collectionsContextMenu;
            tvCollections.MouseDown += (s, e) => {
                if (e.Button == MouseButtons.Right) tvCollections.SelectedNode = tvCollections.GetNodeAt(e.X, e.Y);
            };
            pageCollections.Controls.Add(tvCollections);

            tabSidebar.TabPages.Add(pageHistory);
            tabSidebar.TabPages.Add(pageCollections);
            mainSplitter.Panel1.Controls.Add(tabSidebar);

            // --- Right Panel ---
            tabRequests = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                Padding = new Point(15, 5)
            };
            tabAddButton = new TabPage("  +  ") { Tag = "AddButton" };

            tabRequests.DrawItem += TabRequests_DrawItem;
            tabRequests.MouseDown += TabRequests_MouseDown;
            tabRequests.MouseDoubleClick += TabRequests_MouseDoubleClick;
            tabRequests.Selecting += TabRequests_Selecting;
            mainSplitter.Panel2.Controls.Add(tabRequests);

            this.Controls.Add(mainSplitter);
            mainSplitter.BringToFront();
        }

        private void RefreshHistoryList()
        {
            lstHistory.Items.Clear();
            string query = txtHistorySearch.Text.Trim();
            if (query == "Search history...") query = "";

            foreach (var item in allHistoryItems)
            {
                if (string.IsNullOrWhiteSpace(query) ||
                    (item.CustomName != null && item.CustomName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (item.Url != null && item.Url.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (item.Method != null && item.Method.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    lstHistory.Items.Add(item);
                }
            }
        }

        private void LstHistory_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            var item = lstHistory.Items[e.Index] as RequestHistoryItem;
            if (item == null) return;

            bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            Color bgColor = isSelected ? SystemColors.Highlight : lstHistory.BackColor;
            e.Graphics.FillRectangle(new SolidBrush(bgColor), e.Bounds);

            using (Font methodFont = new Font(lstHistory.Font.FontFamily, lstHistory.Font.Size - 1, FontStyle.Bold))
            using (Font urlFont = new Font(lstHistory.Font.FontFamily, lstHistory.Font.Size))
            {
                Color methodColor;
                switch (item.Method?.ToUpper())
                {
                    case "GET": methodColor = isSelected ? Color.White : Color.MediumSeaGreen; break;
                    case "POST": methodColor = isSelected ? Color.White : Color.Goldenrod; break;
                    case "PUT": methodColor = isSelected ? Color.White : Color.DodgerBlue; break;
                    case "DELETE": methodColor = isSelected ? Color.White : Color.IndianRed; break;
                    case "PATCH": methodColor = isSelected ? Color.White : Color.MediumPurple; break;
                    default: methodColor = isSelected ? Color.White : Color.Gray; break;
                }

                Color urlColor = isSelected ? SystemColors.HighlightText : lstHistory.ForeColor;

                // Draw Method Verb
                string methodStr = (item.Method ?? "").PadRight(6).Substring(0, Math.Min((item.Method ?? "").Length, 6));
                TextRenderer.DrawText(e.Graphics, methodStr, methodFont, new Point(e.Bounds.Left + 2, e.Bounds.Top + 4), methodColor);

                // Draw URL or Custom Name
                int offset = 55; // Fixed offset to align URLs
                string displayStr = !string.IsNullOrWhiteSpace(item.CustomName) ? item.CustomName : item.Url;
                Rectangle urlRect = new Rectangle(e.Bounds.Left + offset, e.Bounds.Top + 4, e.Bounds.Width - offset, e.Bounds.Height - 4);

                TextRenderer.DrawText(e.Graphics, displayStr, urlFont, urlRect, urlColor, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
            e.DrawFocusRectangle();
        }

        private void ImportCollection()
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*", Title = "Import Postman/Swagger Collection" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string json = File.ReadAllText(ofd.FileName);
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            var root = doc.RootElement;
                            if (root.TryGetProperty("info", out var info) && info.TryGetProperty("schema", out var schema) && schema.GetString().Contains("postman"))
                            {
                                ImportPostmanCollection(root);
                            }
                            else if (root.TryGetProperty("openapi", out _) || root.TryGetProperty("swagger", out _))
                            {
                                ImportSwaggerCollection(root);
                            }
                            else
                            {
                                MessageBox.Show("Unrecognized collection format. Please provide a valid Postman Collection v2/v2.1 or OpenAPI/Swagger JSON file.", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to import collection: " + ex.Message, "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ImportPostmanCollection(JsonElement root)
        {
            string colName = "Imported Collection";
            if (root.TryGetProperty("info", out var info) && info.TryGetProperty("name", out var n))
            {
                colName = n.GetString();
            }

            CollectionFolder folder = new CollectionFolder { Name = colName };

            if (root.TryGetProperty("item", out var items))
            {
                ParsePostmanItems(items, folder);
            }

            SaveToCollectionFromImport(folder);
        }

        private void ParsePostmanItems(JsonElement items, CollectionFolder folder)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("request", out var request))
                {
                    var reqItem = new RequestHistoryItem();
                    reqItem.CustomName = item.TryGetProperty("name", out var n) ? n.GetString() : "Unnamed Request";
                    reqItem.Method = request.TryGetProperty("method", out var m) ? m.GetString() : "GET";

                    if (request.TryGetProperty("url", out var url))
                    {
                        if (url.ValueKind == JsonValueKind.String) reqItem.Url = url.GetString();
                        else if (url.TryGetProperty("raw", out var rawUrl)) reqItem.Url = rawUrl.GetString();
                    }

                    if (request.TryGetProperty("header", out var headers) && headers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var header in headers.EnumerateArray())
                        {
                            string key = header.TryGetProperty("key", out var k) ? k.GetString() : "";
                            string val = header.TryGetProperty("value", out var v) ? v.GetString() : "";
                            if (!string.IsNullOrWhiteSpace(key)) reqItem.Headers.Add(new KeyValuePair<string, string>(key, val));
                        }
                    }

                    if (request.TryGetProperty("body", out var body))
                    {
                        if (body.TryGetProperty("mode", out var modeProp))
                        {
                            string mode = modeProp.GetString();
                            if (mode == "raw" && body.TryGetProperty("raw", out var rawBody))
                            {
                                reqItem.BodyType = "raw";
                                reqItem.Body = rawBody.GetString();
                            }
                            else if (mode == "formdata" && body.TryGetProperty("formdata", out var formData))
                            {
                                reqItem.BodyType = "form-data";
                                foreach (var fd in formData.EnumerateArray())
                                {
                                    reqItem.FormData.Add(new FormDataItem
                                    {
                                        Key = fd.TryGetProperty("key", out var fk) ? fd.GetString() : "",
                                        Type = fd.TryGetProperty("type", out var ft) && ft.GetString() == "file" ? "File" : "Text",
                                        Value = fd.TryGetProperty("value", out var fv) ? fv.GetString() : ""
                                    });
                                }
                            }
                            else if (mode == "urlencoded" && body.TryGetProperty("urlencoded", out var urlEncoded))
                            {
                                reqItem.BodyType = "x-www-form-urlencoded";
                                foreach (var ue in urlEncoded.EnumerateArray())
                                {
                                    reqItem.UrlEncodedData.Add(new KeyValuePair<string, string>(
                                        ue.TryGetProperty("key", out var uk) ? uk.GetString() : "",
                                        ue.TryGetProperty("value", out var uv) ? uv.GetString() : ""
                                    ));
                                }
                            }
                        }
                        else if (body.TryGetProperty("raw", out var rawFallback))
                        {
                            reqItem.BodyType = "raw";
                            reqItem.Body = rawFallback.GetString();
                        }
                    }

                    reqItem.Timestamp = DateTime.Now;
                    folder.Requests.Add(reqItem);
                }
                else if (item.TryGetProperty("item", out var subItems))
                {
                    // Flatten nested folders since our UI is currently 1 layer deep
                    ParsePostmanItems(subItems, folder);
                }
            }
        }

        private void ImportSwaggerCollection(JsonElement root)
        {
            string colName = "Swagger API";
            if (root.TryGetProperty("info", out var info) && info.TryGetProperty("title", out var title))
            {
                colName = title.GetString();
            }

            CollectionFolder folder = new CollectionFolder { Name = colName };

            string baseUrl = "";
            if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0)
            {
                baseUrl = servers[0].TryGetProperty("url", out var url) ? url.GetString() : "";
            }
            else if (root.TryGetProperty("host", out var host))
            {
                string scheme = root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array && schemes.GetArrayLength() > 0 ? schemes[0].GetString() : "http";
                string basePath = root.TryGetProperty("basePath", out var bp) ? bp.GetString() : "";
                baseUrl = $"{scheme}://{host.GetString()}{basePath}";
            }

            // Fallback base URL for template mapping
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "{{base_url}}";

            if (root.TryGetProperty("paths", out var paths))
            {
                foreach (var pathProp in paths.EnumerateObject())
                {
                    string path = pathProp.Name;
                    foreach (var methodProp in pathProp.Value.EnumerateObject())
                    {
                        string method = methodProp.Name.ToUpper();
                        if (method == "GET" || method == "POST" || method == "PUT" || method == "DELETE" || method == "PATCH")
                        {
                            var reqItem = new RequestHistoryItem();
                            reqItem.Method = method;

                            // Try to format swagger {param} to postman :param logic if desired, or just leave as is
                            reqItem.Url = baseUrl + path;

                            reqItem.CustomName = methodProp.Value.TryGetProperty("summary", out var summary) ? summary.GetString() : $"{method} {path}";
                            reqItem.Timestamp = DateTime.Now;

                            folder.Requests.Add(reqItem);
                        }
                    }
                }
            }

            SaveToCollectionFromImport(folder);
        }

        private void SaveToCollectionFromImport(CollectionFolder newFolder)
        {
            TreeNode colNode = new TreeNode(newFolder.Name);
            foreach (var req in newFolder.Requests)
            {
                string nodeName = !string.IsNullOrWhiteSpace(req.CustomName) ? req.CustomName : (!string.IsNullOrWhiteSpace(req.Url) ? req.Url : "New Request");
                TreeNode reqNode = new TreeNode(nodeName) { Tag = req };
                colNode.Nodes.Add(reqNode);
            }
            tvCollections.Nodes.Add(colNode);

            SaveData(); // Persist changes

            tabSidebar.SelectedTab = tabSidebar.TabPages[1]; // Focus collections
            MessageBox.Show($"Successfully imported {newFolder.Requests.Count} requests into '{newFolder.Name}'.", "Import Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ApiTesterForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveData();
        }

        private void SaveData()
        {
            try
            {
                var appState = new AppState();

                appState.EnvironmentVariables = EnvManager.Variables;
                appState.History = allHistoryItems;

                foreach (TreeNode colNode in tvCollections.Nodes)
                {
                    var folder = new CollectionFolder { Name = colNode.Text };
                    foreach (TreeNode reqNode in colNode.Nodes)
                    {
                        if (reqNode.Tag is RequestHistoryItem reqItem) folder.Requests.Add(reqItem);
                    }
                    appState.Collections.Add(folder);
                }

                string json = JsonSerializer.Serialize(appState, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dataFilePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving data: " + ex.Message, "Save Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadData()
        {
            try
            {
                if (File.Exists(dataFilePath))
                {
                    string json = File.ReadAllText(dataFilePath);
                    var appState = JsonSerializer.Deserialize<AppState>(json);

                    if (appState != null)
                    {
                        if (appState.EnvironmentVariables != null) EnvManager.Variables = appState.EnvironmentVariables;

                        if (appState.History != null)
                        {
                            allHistoryItems = appState.History;
                            RefreshHistoryList();
                        }

                        if (appState.Collections != null)
                        {
                            foreach (var folder in appState.Collections)
                            {
                                TreeNode colNode = new TreeNode(folder.Name);
                                if (folder.Requests != null)
                                {
                                    foreach (var req in folder.Requests)
                                    {
                                        string nodeName = !string.IsNullOrWhiteSpace(req.CustomName) ? req.CustomName : (!string.IsNullOrWhiteSpace(req.Url) ? req.Url : "New Request");
                                        TreeNode reqNode = new TreeNode(nodeName) { Tag = req };
                                        colNode.Nodes.Add(reqNode);
                                    }
                                }
                                tvCollections.Nodes.Add(colNode);
                            }
                        }
                    }
                }
            }
            catch { /* Ignore load errors on fresh start */ }
        }

        private void ShowEnvManagerDialog()
        {
            Form envForm = new Form() { Width = 500, Height = 400, FormBorderStyle = FormBorderStyle.FixedDialog, Text = "Environment Variables (Global)", StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };

            Label lblInfo = new Label() { Text = "Define variables here. Use {{Variable_Name}} in your URLs, Headers, or Body.", Dock = DockStyle.Top, Height = 30, Padding = new Padding(5) };

            DataGridView dgv = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, AllowUserToAddRows = true };
            dgv.Columns.Add("Key", "Variable Name (e.g. base_url)");
            dgv.Columns.Add("Value", "Value");

            foreach (var kv in EnvManager.Variables) dgv.Rows.Add(kv.Key, kv.Value);

            Panel pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 45 };
            Button btnSave = new Button { Text = "Save Variables", Left = 340, Top = 8, Width = 130, DialogResult = DialogResult.OK, BackColor = Color.DodgerBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            pnlBottom.Controls.Add(btnSave);

            envForm.Controls.Add(dgv);
            envForm.Controls.Add(lblInfo);
            envForm.Controls.Add(pnlBottom);

            if (envForm.ShowDialog(this) == DialogResult.OK)
            {
                EnvManager.Variables.Clear();
                foreach (DataGridViewRow row in dgv.Rows)
                {
                    if (!row.IsNewRow)
                    {
                        string k = row.Cells[0].Value?.ToString();
                        string v = row.Cells[1].Value?.ToString();
                        if (!string.IsNullOrWhiteSpace(k)) EnvManager.Variables[k.Trim()] = v ?? "";
                    }
                }
            }
        }

        private void ApiTesterForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                if (tabRequests.SelectedTab != null && tabRequests.SelectedTab.Controls.Count > 0)
                {
                    if (tabRequests.SelectedTab.Controls[0] is RequestWorkspace workspace)
                    {
                        workspace.ShowSearch();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (tabRequests.SelectedTab != null && tabRequests.SelectedTab.Controls.Count > 0)
                {
                    if (tabRequests.SelectedTab.Controls[0] is RequestWorkspace workspace)
                    {
                        if (workspace.HideSearch())
                        {
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                        }
                    }
                }
            }
        }

        private void TabRequests_Selecting(object sender, TabControlCancelEventArgs e)
        {
            if (e.TabPage != null && e.TabPage.Tag as string == "AddButton")
            {
                e.Cancel = true;
                this.BeginInvoke(new Action(() => AddNewTab()));
            }
        }

        private void TvCollections_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node.Tag is RequestHistoryItem item) AddNewTab(item);
        }

        private void TabRequests_DrawItem(object sender, DrawItemEventArgs e)
        {
            var tabPage = tabRequests.TabPages[e.Index];
            var tabRect = tabRequests.GetTabRect(e.Index);

            Color bgNormal = SystemColors.ControlLight;
            Color bgSelected = SystemColors.Window;
            Color fgText = SystemColors.ControlText;

            if (tabPage.Tag as string == "AddButton")
            {
                e.Graphics.FillRectangle(new SolidBrush(bgNormal), tabRect);
                using (Font addFont = new Font("Segoe UI", 12, FontStyle.Bold))
                {
                    TextRenderer.DrawText(e.Graphics, "+", addFont, tabRect, Color.DimGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                return;
            }

            if ((e.State & DrawItemState.Selected) == DrawItemState.Selected)
            {
                e.Graphics.FillRectangle(new SolidBrush(bgSelected), tabRect);
            }
            else
            {
                e.Graphics.FillRectangle(new SolidBrush(bgNormal), tabRect);
            }

            TextRenderer.DrawText(e.Graphics, tabPage.Text, tabPage.Font, tabRect, fgText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var closeRect = new Rectangle(tabRect.Right - 15, tabRect.Top + 6, 10, 10);
            using (Font closeFont = new Font("Consolas", 8, FontStyle.Bold))
            {
                e.Graphics.DrawString("X", closeFont, Brushes.DimGray, closeRect.Location);
            }
            e.DrawFocusRectangle();
        }

        private void TabRequests_MouseDown(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < tabRequests.TabPages.Count; i++)
            {
                if (tabRequests.TabPages[i].Tag as string == "AddButton") continue;

                Rectangle tabRect = tabRequests.GetTabRect(i);
                Rectangle closeHitArea = new Rectangle(tabRect.Right - 20, tabRect.Top + 2, 18, 18);

                if (closeHitArea.Contains(e.Location))
                {
                    int standardTabCount = tabRequests.TabPages.Count - (tabRequests.TabPages.Contains(tabAddButton) ? 1 : 0);

                    if (standardTabCount > 1) tabRequests.TabPages.RemoveAt(i);
                    else
                    {
                        if (tabRequests.TabPages[i].Controls.Count > 0 && tabRequests.TabPages[i].Controls[0] is RequestWorkspace workspace)
                        {
                            workspace.ClearRequest();
                            tabRequests.TabPages[i].Text = "New Request";
                            tabRequests.TabPages[i].Tag = null;
                            tabRequests.Invalidate();
                        }
                    }
                    break;
                }
            }
        }

        private void TabRequests_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < tabRequests.TabPages.Count; i++)
            {
                if (tabRequests.TabPages[i].Tag as string == "AddButton") continue;

                Rectangle tabRect = tabRequests.GetTabRect(i);
                Rectangle closeHitArea = new Rectangle(tabRect.Right - 20, tabRect.Top + 2, 18, 18);

                if (tabRect.Contains(e.Location) && !closeHitArea.Contains(e.Location))
                {
                    string newName = PromptForInput("Rename Request", "Enter new name for this tab:", tabRequests.TabPages[i].Text);
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        tabRequests.TabPages[i].Text = newName;
                        tabRequests.TabPages[i].Tag = "Renamed";
                        tabRequests.Invalidate();
                    }
                    break;
                }
            }
        }

        private string PromptForInput(string title, string promptText, string defaultValue)
        {
            Form prompt = new Form()
            {
                Width = 400,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };
            Label textLabel = new Label() { Left = 15, Top = 15, Width = 350, Text = promptText };
            TextBox inputBox = new TextBox() { Left = 15, Top = 35, Width = 350, Text = defaultValue };
            Button confirmation = new Button() { Text = "OK", Left = 195, Width = 80, Top = 70, DialogResult = DialogResult.OK };
            Button cancel = new Button() { Text = "Cancel", Left = 285, Width = 80, Top = 70, DialogResult = DialogResult.Cancel };

            prompt.Controls.Add(textLabel);
            prompt.Controls.Add(inputBox);
            prompt.Controls.Add(confirmation);
            prompt.Controls.Add(cancel);
            prompt.AcceptButton = confirmation;
            prompt.CancelButton = cancel;

            return prompt.ShowDialog(this) == DialogResult.OK ? inputBox.Text : null;
        }

        private void ShowSaveToCollectionDialog(RequestHistoryItem item, TabPage parentPage)
        {
            Form prompt = new Form()
            {
                Width = 400,
                Height = 220,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                Text = "Save Request to Collection",
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };

            Label lblCol = new Label() { Left = 15, Top = 15, Width = 350, Text = "Collection Name (Select or type new):" };
            ComboBox cmbCollections = new ComboBox() { Left = 15, Top = 35, Width = 350 };
            foreach (TreeNode node in tvCollections.Nodes) cmbCollections.Items.Add(node.Text);

            Label lblReq = new Label() { Left = 15, Top = 70, Width = 350, Text = "Request Name:" };
            TextBox txtReq = new TextBox() { Left = 15, Top = 90, Width = 350, Text = item.CustomName ?? parentPage.Text };

            Button confirmation = new Button() { Text = "Save", Left = 195, Width = 80, Top = 130, DialogResult = DialogResult.OK, BackColor = Color.MediumSeaGreen, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            Button cancel = new Button() { Text = "Cancel", Left = 285, Width = 80, Top = 130, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };

            prompt.Controls.Add(lblCol); prompt.Controls.Add(cmbCollections); prompt.Controls.Add(lblReq);
            prompt.Controls.Add(txtReq); prompt.Controls.Add(confirmation); prompt.Controls.Add(cancel);
            prompt.AcceptButton = confirmation; prompt.CancelButton = cancel;

            if (prompt.ShowDialog(this) == DialogResult.OK)
            {
                string colName = string.IsNullOrWhiteSpace(cmbCollections.Text) ? "Uncategorized" : cmbCollections.Text;
                item.CustomName = string.IsNullOrWhiteSpace(txtReq.Text) ? (string.IsNullOrWhiteSpace(item.Url) ? "New Request" : item.Url) : txtReq.Text;

                parentPage.Text = item.CustomName;
                parentPage.Tag = "Renamed";
                tabRequests.Invalidate();

                SaveToCollection(colName, item);
                tabSidebar.SelectedTab = tabSidebar.TabPages[1];
            }
        }

        private void SaveToCollection(string collectionName, RequestHistoryItem item)
        {
            TreeNode colNode = null;
            foreach (TreeNode node in tvCollections.Nodes)
            {
                if (node.Text.Equals(collectionName, StringComparison.OrdinalIgnoreCase))
                {
                    colNode = node;
                    break;
                }
            }
            if (colNode == null)
            {
                colNode = new TreeNode(collectionName);
                tvCollections.Nodes.Add(colNode);
            }
            TreeNode reqNode = new TreeNode(item.CustomName) { Tag = item };
            colNode.Nodes.Add(reqNode);
            colNode.Expand();
        }

        private void AddNewTab(RequestHistoryItem historyItem = null)
        {
            TabPage page = new TabPage("New Request");
            RequestWorkspace workspace = new RequestWorkspace { Dock = DockStyle.Fill };

            workspace.OnRequestSent += (hItem) => {
                allHistoryItems.Insert(0, hItem);
                RefreshHistoryList();
                if (page.Tag as string != "Renamed")
                {
                    try { page.Text = $"{hItem.Method} {new Uri(hItem.Url).Host}"; } catch { page.Text = hItem.Method; }
                    tabRequests.Invalidate();
                }
            };

            workspace.OnSaveRequest += (reqToSave) => {
                if (page.Tag as string == "Renamed") reqToSave.CustomName = page.Text;
                ShowSaveToCollectionDialog(reqToSave, page);
            };

            if (historyItem != null)
            {
                workspace.LoadFromHistory(historyItem);
                if (page.Tag as string != "Renamed")
                {
                    if (!string.IsNullOrWhiteSpace(historyItem.CustomName))
                    {
                        page.Text = historyItem.CustomName;
                        page.Tag = "Renamed";
                    }
                    else
                    {
                        try { page.Text = $"{historyItem.Method} {new Uri(historyItem.Url).Host}"; } catch { page.Text = historyItem.Method; }
                    }
                }
            }

            page.Controls.Add(workspace);

            if (tabAddButton != null && tabRequests.TabPages.Contains(tabAddButton))
                tabRequests.TabPages.Insert(tabRequests.TabPages.Count - 1, page);
            else
                tabRequests.TabPages.Add(page);

            tabRequests.SelectedTab = page;
        }

        private void CloseCurrentTab()
        {
            int standardTabCount = tabRequests.TabPages.Count - (tabRequests.TabPages.Contains(tabAddButton) ? 1 : 0);

            if (standardTabCount > 1 && tabRequests.SelectedTab != null && tabRequests.SelectedTab != tabAddButton)
            {
                tabRequests.TabPages.Remove(tabRequests.SelectedTab);
            }
            else if (standardTabCount == 1 && tabRequests.SelectedTab != tabAddButton)
            {
                if (tabRequests.SelectedTab.Controls.Count > 0 && tabRequests.SelectedTab.Controls[0] is RequestWorkspace workspace)
                {
                    workspace.ClearRequest();
                    tabRequests.SelectedTab.Text = "New Request";
                    tabRequests.SelectedTab.Tag = null;
                    tabRequests.Invalidate();
                }
            }
        }

        private void LstHistory_DoubleClick(object sender, EventArgs e)
        {
            if (lstHistory.SelectedItem is RequestHistoryItem item) AddNewTab(item);
        }
    }

    // --- Workspace UserControl (The actual request UI) ---
    public class RequestWorkspace : UserControl
    {
        public event Action<RequestHistoryItem> OnRequestSent;
        public event Action<RequestHistoryItem> OnSaveRequest;

        // UI Controls
        private ComboBox cmbMethod;
        private TextBox txtUrl;
        private Button btnSend;
        private Button btnCodeSnippet;
        private Button btnImportCurl;
        private Button btnClear;
        private Button btnSave;
        private SplitContainer splitContainerMain;
        private TabControl tabRequestDetails;
        private TabPage tabParams;
        private TabPage tabAuth;
        private TabPage tabHeaders;
        private TabPage tabBody;
        private DataGridView dgvParams;
        private DataGridView dgvHeaders;

        // Advanced Body Controls
        private RadioButton rbBodyRaw;
        private RadioButton rbBodyFormData;
        private RadioButton rbBodyUrlEncoded;
        private RichTextBox txtRequestBody;
        private DataGridView dgvFormData;
        private DataGridView dgvUrlEncoded;

        private Label lblStatusBadge;
        private Label lblStatusText;
        private Button btnCopyResponse;
        private Panel pnlResponseTop;

        // Response Tab Controls
        private TabControl tabResponse;
        private TabPage tabPageResponseBody;
        private TabPage tabPageResponseHeaders;
        private RichTextBox txtResponse;
        private DataGridView dgvResponseHeaders;

        // Search panel controls
        private Panel pnlSearch;
        private TextBox txtSearch;
        private Button btnFindNext;
        private Button btnCloseSearch;
        private int lastSearchIndex = 0;

        // Cancellation Token
        private CancellationTokenSource _cts;

        // Auth Controls
        private ComboBox cmbAuthType;
        private Panel pnlAuthContent;

        // Auth - API Key
        private Label lblApiKeyName;
        private TextBox txtApiKeyName;
        private Label lblApiKeyValue;
        private TextBox txtApiKeyValue;
        private Label lblApiKeyAddTo;
        private ComboBox cmbApiKeyAddTo;

        // Auth - Bearer / Basic
        private Label lblBearerToken;
        private TextBox txtBearerToken;
        private Label lblBasicUser;
        private TextBox txtBasicUser;
        private Label lblBasicPass;
        private TextBox txtBasicPass;

        // Auth - OAuth 2.0
        private Label lblOAuth2AuthUrl, lblOAuth2TokenUrl, lblOAuth2ClientId, lblOAuth2ClientSecret, lblOAuth2Scope, lblOAuth2RedirectUri, lblOAuth2Token;
        private TextBox txtOAuth2AuthUrl, txtOAuth2TokenUrl, txtOAuth2ClientId, txtOAuth2ClientSecret, txtOAuth2Scope, txtOAuth2RedirectUri, txtOAuth2Token;
        private Button btnGetOAuth2Token;

        private bool isSyncingParams = false;

        public RequestWorkspace()
        {
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.Width = 800;

            // --- Top Panel ---
            Panel pnlTop = new Panel { Dock = DockStyle.Top, Height = 50, Width = this.Width, Padding = new Padding(10) };

            cmbMethod = new ComboBox { Left = 10, Top = 12, Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbMethod.Items.AddRange(new string[] { "GET", "POST", "PUT", "DELETE", "PATCH" });
            cmbMethod.SelectedIndex = 0;

            txtUrl = new TextBox
            {
                Left = 100,
                Top = 12,
                Width = this.Width - 570,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = ""
            };
            txtUrl.TextChanged += (s, e) => SyncUrlToParams();

            btnSave = new Button { Left = this.Width - 460, Top = 10, Width = 70, Height = 25, Text = "💾 Save", Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.MediumSeaGreen, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnSave.Click += (s, e) => TriggerSave();

            btnClear = new Button { Left = this.Width - 380, Top = 10, Width = 70, Height = 25, Text = "Clear", Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.IndianRed, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnClear.Click += (s, e) => ClearRequest();

            btnImportCurl = new Button { Left = this.Width - 300, Top = 10, Width = 90, Height = 25, Text = "Import cURL", Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.LightGray, FlatStyle = FlatStyle.Flat };
            btnImportCurl.Click += (s, e) => ShowImportCurlDialog();

            btnCodeSnippet = new Button { Left = this.Width - 200, Top = 10, Width = 90, Height = 25, Text = "</> Code", Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.LightGray, FlatStyle = FlatStyle.Flat };
            btnCodeSnippet.Click += (s, e) => ShowCodeSnippetDialog();

            btnSend = new Button { Left = this.Width - 100, Top = 10, Width = 90, Height = 25, Text = "Send", Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Color.DodgerBlue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnSend.Click += async (s, e) => await HandleSendClickAsync();

            pnlTop.Controls.Add(cmbMethod); pnlTop.Controls.Add(txtUrl); pnlTop.Controls.Add(btnSave);
            pnlTop.Controls.Add(btnClear); pnlTop.Controls.Add(btnImportCurl); pnlTop.Controls.Add(btnCodeSnippet);
            pnlTop.Controls.Add(btnSend);

            // --- Main Split Container ---
            splitContainerMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 20,
                Padding = new Padding(10)
            };

            // --- Request Section (Top Half) ---
            tabRequestDetails = new TabControl { Dock = DockStyle.Fill };
            tabParams = new TabPage("Params");
            tabAuth = new TabPage("Authorization");
            tabHeaders = new TabPage("Headers");
            tabBody = new TabPage("Body");

            // Setup Params DataGridView
            dgvParams = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, AllowUserToAddRows = true };
            dgvParams.Columns.Add("Key", "Key"); dgvParams.Columns.Add("Value", "Value");
            dgvParams.CellValueChanged += (s, e) => SyncParamsToUrl();
            dgvParams.RowsRemoved += (s, e) => SyncParamsToUrl();
            tabParams.Controls.Add(dgvParams);

            SetupAuthTab();

            // Setup Headers DataGridView
            dgvHeaders = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, AllowUserToAddRows = true };
            dgvHeaders.Columns.Add("Key", "Key"); dgvHeaders.Columns.Add("Value", "Value");
            tabHeaders.Controls.Add(dgvHeaders);

            // Setup Body Tab (Raw, Form-Data, UrlEncoded)
            Panel pnlBodyOptions = new Panel { Dock = DockStyle.Top, Height = 30 };
            rbBodyRaw = new RadioButton { Text = "raw (JSON)", Left = 10, Top = 5, AutoSize = true, Checked = true };
            rbBodyFormData = new RadioButton { Text = "form-data", Left = 100, Top = 5, AutoSize = true };
            rbBodyUrlEncoded = new RadioButton { Text = "x-www-form-urlencoded", Left = 190, Top = 5, AutoSize = true };

            pnlBodyOptions.Controls.Add(rbBodyRaw);
            pnlBodyOptions.Controls.Add(rbBodyFormData);
            pnlBodyOptions.Controls.Add(rbBodyUrlEncoded);

            txtRequestBody = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 10), AcceptsTab = true, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both, BorderStyle = BorderStyle.None };

            // Format and highlight JSON when the user finishes editing the raw body
            txtRequestBody.Leave += (s, e) => {
                if (rbBodyRaw.Checked && !string.IsNullOrWhiteSpace(txtRequestBody.Text))
                {
                    try
                    {
                        string formatted = TryFormatJson(txtRequestBody.Text);
                        txtRequestBody.Rtf = GenerateRtfFromJson(formatted);
                    }
                    catch { } // Ignore format errors on partial JSON
                }
            };

            // Form-Data DataGridView
            dgvFormData = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, AllowUserToAddRows = true, Visible = false };
            dgvFormData.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "Key" });
            dgvFormData.Columns.Add(new DataGridViewComboBoxColumn { Name = "Type", HeaderText = "Type", Items = { "Text", "File" } });
            dgvFormData.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value / File Path" });
            dgvFormData.Columns.Add(new DataGridViewButtonColumn { Name = "Browse", HeaderText = "...", Text = "...", UseColumnTextForButtonValue = true, Width = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });

            dgvFormData.DefaultValuesNeeded += (s, e) => { e.Row.Cells["Type"].Value = "Text"; };
            dgvFormData.CellClick += (s, e) => {
                if (e.RowIndex >= 0 && e.ColumnIndex == 3)
                {
                    if (dgvFormData.Rows[e.RowIndex].Cells["Type"].Value?.ToString() == "File")
                    {
                        using (OpenFileDialog ofd = new OpenFileDialog())
                        {
                            if (ofd.ShowDialog() == DialogResult.OK)
                            {
                                dgvFormData.Rows[e.RowIndex].Cells["Value"].Value = ofd.FileName;
                            }
                        }
                    }
                }
            };

            // UrlEncoded DataGridView
            dgvUrlEncoded = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, AllowUserToAddRows = true, Visible = false };
            dgvUrlEncoded.Columns.Add("Key", "Key"); dgvUrlEncoded.Columns.Add("Value", "Value");

            // Event handler to toggle body visibility
            EventHandler toggleBodyTypes = (s, e) => {
                txtRequestBody.Visible = rbBodyRaw.Checked;
                dgvFormData.Visible = rbBodyFormData.Checked;
                dgvUrlEncoded.Visible = rbBodyUrlEncoded.Checked;
            };
            rbBodyRaw.CheckedChanged += toggleBodyTypes;
            rbBodyFormData.CheckedChanged += toggleBodyTypes;
            rbBodyUrlEncoded.CheckedChanged += toggleBodyTypes;

            tabBody.Controls.Add(txtRequestBody);
            tabBody.Controls.Add(dgvFormData);
            tabBody.Controls.Add(dgvUrlEncoded);
            tabBody.Controls.Add(pnlBodyOptions);

            // Ensure Z-order so DockStyle.Fill doesn't overlap the top options panel
            pnlBodyOptions.SendToBack();
            txtRequestBody.BringToFront();
            dgvFormData.BringToFront();
            dgvUrlEncoded.BringToFront();

            tabRequestDetails.TabPages.Add(tabParams); tabRequestDetails.TabPages.Add(tabAuth);
            tabRequestDetails.TabPages.Add(tabHeaders); tabRequestDetails.TabPages.Add(tabBody);
            splitContainerMain.Panel1.Controls.Add(tabRequestDetails);

            // --- Response Section (Bottom Half) ---
            pnlResponseTop = new Panel { Dock = DockStyle.Top, Height = 34, Width = this.Width, Padding = new Padding(0) };

            lblStatusBadge = new Label
            {
                Left = 0,
                Top = 5,
                Width = 50,
                Height = 24,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.White,
                Visible = false
            };
            lblStatusText = new Label
            {
                Left = 55,
                Top = 9,
                Width = this.Width - 160,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Text = "Waiting for request...",
                Font = new Font("Segoe UI", 9, FontStyle.Regular)
            };

            btnCopyResponse = new Button { Left = this.Width - 95, Top = 5, Width = 80, Height = 24, Anchor = AnchorStyles.Right | AnchorStyles.Top, Text = "📋 Copy", FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            btnCopyResponse.FlatAppearance.BorderColor = Color.LightGray;
            btnCopyResponse.Click += BtnCopyResponse_Click;

            pnlResponseTop.Controls.Add(lblStatusBadge);
            pnlResponseTop.Controls.Add(lblStatusText);
            pnlResponseTop.Controls.Add(btnCopyResponse);

            tabResponse = new TabControl { Dock = DockStyle.Fill };
            tabPageResponseBody = new TabPage("Body");
            tabPageResponseHeaders = new TabPage("Headers");

            txtResponse = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ScrollBars = RichTextBoxScrollBars.Both,
                ReadOnly = true,
                Font = new Font("Consolas", 10),
                WordWrap = false,
                HideSelection = false
            };

            pnlSearch = new Panel { Visible = false, Height = 32, Width = 265, BorderStyle = BorderStyle.FixedSingle };
            txtSearch = new TextBox { Left = 5, Top = 4, Width = 140 };
            btnFindNext = new Button { Left = 150, Top = 3, Width = 50, Height = 24, Text = "Find", FlatStyle = FlatStyle.Flat };
            btnCloseSearch = new Button { Left = 205, Top = 3, Width = 50, Height = 24, Text = "Close", FlatStyle = FlatStyle.Flat };

            btnFindNext.Click += BtnFindNext_Click;
            btnCloseSearch.Click += (s, e) => HideSearch();
            txtSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { BtnFindNext_Click(null, null); e.Handled = true; e.SuppressKeyPress = true; } };
            txtSearch.TextChanged += (s, e) => lastSearchIndex = 0;

            pnlSearch.Controls.Add(txtSearch); pnlSearch.Controls.Add(btnFindNext); pnlSearch.Controls.Add(btnCloseSearch);

            tabPageResponseBody.Controls.Add(pnlSearch);
            tabPageResponseBody.Controls.Add(txtResponse);

            dgvResponseHeaders = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                ReadOnly = true,
                AllowUserToAddRows = false,
                RowHeadersVisible = false
            };
            dgvResponseHeaders.Columns.Add("Key", "Key"); dgvResponseHeaders.Columns.Add("Value", "Value");
            tabPageResponseHeaders.Controls.Add(dgvResponseHeaders);

            tabResponse.TabPages.Add(tabPageResponseBody);
            tabResponse.TabPages.Add(tabPageResponseHeaders);

            splitContainerMain.Panel2.Controls.Add(tabResponse);
            splitContainerMain.Panel2.Controls.Add(pnlResponseTop);

            // Ensure proper docking z-order for the response panel
            pnlResponseTop.SendToBack();
            tabResponse.BringToFront();

            tabPageResponseBody.Resize += (s, e) => {
                pnlSearch.Left = tabPageResponseBody.Width - pnlSearch.Width - 20;
                pnlSearch.Top = 5;
            };

            this.Controls.Add(splitContainerMain);
            this.Controls.Add(pnlTop);
            SyncUrlToParams();
        }

        private void BtnCopyResponse_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(txtResponse.Text))
            {
                try
                {
                    Clipboard.SetText(txtResponse.Text);
                    btnCopyResponse.Text = "✔ Copied";

                    Task.Delay(2000).ContinueWith(_ => {
                        if (this.IsHandleCreated && !this.IsDisposed)
                        {
                            this.Invoke(new Action(() => {
                                if (!btnCopyResponse.IsDisposed)
                                    btnCopyResponse.Text = "📋 Copy";
                            }));
                        }
                    });
                }
                catch { /* Ignore Windows Clipboard locking issues */ }
            }
        }

        public void ShowSearch()
        {
            if (tabResponse.SelectedTab != tabPageResponseBody) tabResponse.SelectedTab = tabPageResponseBody;
            pnlSearch.Left = tabPageResponseBody.Width - pnlSearch.Width - 20;
            pnlSearch.Top = 5;
            pnlSearch.Visible = true; pnlSearch.BringToFront();
            txtSearch.Focus(); txtSearch.SelectAll();
        }

        public bool HideSearch()
        {
            if (pnlSearch.Visible) { pnlSearch.Visible = false; txtResponse.Focus(); return true; }
            return false;
        }

        private void BtnFindNext_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSearch.Text)) return;
            if (lastSearchIndex >= txtResponse.TextLength) lastSearchIndex = 0;
            int index = txtResponse.Find(txtSearch.Text, lastSearchIndex, RichTextBoxFinds.None);
            if (index == -1 && lastSearchIndex > 0) index = txtResponse.Find(txtSearch.Text, 0, RichTextBoxFinds.None);

            if (index != -1)
            {
                txtResponse.Select(index, txtSearch.Text.Length);
                txtResponse.ScrollToCaret();
                lastSearchIndex = index + txtSearch.Text.Length;
            }
            else { MessageBox.Show("No matches found.", "Find", MessageBoxButtons.OK, MessageBoxIcon.Information); lastSearchIndex = 0; }
        }

        private void TriggerSave()
        {
            var historyItem = GetCurrentRequestSnapshot();
            historyItem.Timestamp = DateTime.Now;
            OnSaveRequest?.Invoke(historyItem);
        }

        // Extracts all currently configured request data dynamically for Export/Sending/Saving
        private RequestHistoryItem GetCurrentRequestSnapshot()
        {
            var item = new RequestHistoryItem
            {
                Method = cmbMethod.Text,
                Url = txtUrl.Text,
                Body = txtRequestBody.Text,
                AuthType = cmbAuthType.SelectedItem?.ToString() ?? "No Auth",
                ApiKeyName = txtApiKeyName.Text,
                ApiKeyValue = txtApiKeyValue.Text,
                ApiKeyAddTo = cmbApiKeyAddTo.SelectedItem?.ToString() ?? "Header",
                AuthToken = txtBearerToken.Text,
                AuthUser = txtBasicUser.Text,
                AuthPass = txtBasicPass.Text,
                OAuth2AuthUrl = txtOAuth2AuthUrl.Text,
                OAuth2TokenUrl = txtOAuth2TokenUrl.Text,
                OAuth2ClientId = txtOAuth2ClientId.Text,
                OAuth2ClientSecret = txtOAuth2ClientSecret.Text,
                OAuth2Scope = txtOAuth2Scope.Text,
                OAuth2RedirectUri = txtOAuth2RedirectUri.Text,
                OAuth2Token = txtOAuth2Token.Text
            };

            if (rbBodyRaw.Checked) item.BodyType = "raw";
            else if (rbBodyFormData.Checked) item.BodyType = "form-data";
            else if (rbBodyUrlEncoded.Checked) item.BodyType = "x-www-form-urlencoded";

            foreach (DataGridViewRow row in dgvHeaders.Rows)
            {
                if (!row.IsNewRow && !string.IsNullOrWhiteSpace(row.Cells[0].Value?.ToString()))
                    item.Headers.Add(new KeyValuePair<string, string>(row.Cells[0].Value.ToString(), row.Cells[1].Value?.ToString() ?? ""));
            }

            foreach (DataGridViewRow row in dgvFormData.Rows)
            {
                if (!row.IsNewRow && !string.IsNullOrWhiteSpace(row.Cells[0].Value?.ToString()))
                    item.FormData.Add(new FormDataItem { Key = row.Cells[0].Value.ToString(), Type = row.Cells[1].Value?.ToString() ?? "Text", Value = row.Cells[2].Value?.ToString() ?? "" });
            }

            foreach (DataGridViewRow row in dgvUrlEncoded.Rows)
            {
                if (!row.IsNewRow && !string.IsNullOrWhiteSpace(row.Cells[0].Value?.ToString()))
                    item.UrlEncodedData.Add(new KeyValuePair<string, string>(row.Cells[0].Value.ToString(), row.Cells[1].Value?.ToString() ?? ""));
            }
            return item;
        }

        private void ShowCodeSnippetDialog()
        {
            var req = GetCurrentRequestSnapshot();

            Form snippetForm = new Form() { Width = 700, Height = 500, FormBorderStyle = FormBorderStyle.FixedDialog, Text = "Generate Code Snippet", StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };

            ListBox lstLang = new ListBox() { Left = 10, Top = 10, Width = 150, Height = 400 };
            lstLang.Items.AddRange(new string[] { "cURL", "C# (HttpClient)", "Python (Requests)", "JavaScript (Fetch)", "Go (Native)" });

            RichTextBox txtCode = new RichTextBox() { Left = 170, Top = 10, Width = 500, Height = 400, Font = new Font("Consolas", 10), ReadOnly = true, WordWrap = false };
            Button btnCopy = new Button() { Text = "📋 Copy Code", Left = 570, Top = 420, Width = 100, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.DodgerBlue, ForeColor = Color.White };

            lstLang.SelectedIndexChanged += (s, e) => {
                string lang = lstLang.SelectedItem.ToString();
                if (lang.StartsWith("cURL")) txtCode.Text = GenerateCurl(req);
                else if (lang.StartsWith("C#")) txtCode.Text = GenerateCSharp(req);
                else if (lang.StartsWith("Python")) txtCode.Text = GeneratePython(req);
                else if (lang.StartsWith("Java")) txtCode.Text = GenerateJS(req);
                else if (lang.StartsWith("Go")) txtCode.Text = GenerateGo(req);
            };

            btnCopy.Click += (s, e) => {
                if (!string.IsNullOrWhiteSpace(txtCode.Text))
                {
                    Clipboard.SetText(txtCode.Text);
                    btnCopy.Text = "✔ Copied";
                    Task.Delay(2000).ContinueWith(_ => { if (snippetForm.IsHandleCreated) snippetForm.Invoke(new Action(() => { btnCopy.Text = "📋 Copy Code"; })); });
                }
            };

            snippetForm.Controls.Add(lstLang);
            snippetForm.Controls.Add(txtCode);
            snippetForm.Controls.Add(btnCopy);

            lstLang.SelectedIndex = 0; // Trigger generation
            snippetForm.ShowDialog(this);
        }

        private string GenerateCurl(RequestHistoryItem req)
        {
            string finalUrl = EnvManager.Replace(req.Url);
            if (req.AuthType == "API Key" && !string.IsNullOrWhiteSpace(req.ApiKeyName) && req.ApiKeyAddTo == "Query Params")
            {
                finalUrl += (finalUrl.Contains("?") ? "&" : "?") + $"{Uri.EscapeDataString(EnvManager.Replace(req.ApiKeyName))}={Uri.EscapeDataString(EnvManager.Replace(req.ApiKeyValue))}";
            }

            StringBuilder sb = new StringBuilder();
            sb.Append($"curl -X {req.Method} \"{finalUrl}\"");

            if (req.AuthType == "Bearer Token" && !string.IsNullOrWhiteSpace(req.AuthToken)) sb.Append($" -H \"Authorization: Bearer {EnvManager.Replace(req.AuthToken)}\"");
            else if (req.AuthType == "OAuth 2.0" && !string.IsNullOrWhiteSpace(req.OAuth2Token)) sb.Append($" -H \"Authorization: Bearer {EnvManager.Replace(req.OAuth2Token)}\"");
            else if (req.AuthType == "Basic Auth" && (!string.IsNullOrEmpty(req.AuthUser) || !string.IsNullOrEmpty(req.AuthPass)))
            {
                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{EnvManager.Replace(req.AuthUser)}:{EnvManager.Replace(req.AuthPass)}"));
                sb.Append($" -H \"Authorization: Basic {authString}\"");
            }
            else if (req.AuthType == "API Key" && !string.IsNullOrWhiteSpace(req.ApiKeyName) && req.ApiKeyAddTo == "Header") sb.Append($" -H \"{EnvManager.Replace(req.ApiKeyName)}: {EnvManager.Replace(req.ApiKeyValue)}\"");

            bool hasContentType = false;
            foreach (var h in req.Headers)
            {
                sb.Append($" -H \"{EnvManager.Replace(h.Key)}: {EnvManager.Replace(h.Value)}\"");
                if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) hasContentType = true;
            }

            if (req.Method != "GET")
            {
                if (req.BodyType == "raw" && !string.IsNullOrWhiteSpace(req.Body))
                {
                    if (!hasContentType) sb.Append(" -H \"Content-Type: application/json\"");
                    sb.Append($" -d '{EnvManager.Replace(req.Body).Replace("'", "'\\''")}'");
                }
                else if (req.BodyType == "x-www-form-urlencoded")
                {
                    foreach (var kv in req.UrlEncodedData)
                    {
                        sb.Append($" -d \"{EnvManager.Replace(kv.Key)}={EnvManager.Replace(kv.Value)}\"");
                    }
                }
                else if (req.BodyType == "form-data")
                {
                    foreach (var fd in req.FormData)
                    {
                        if (fd.Type == "File") sb.Append($" -F \"{EnvManager.Replace(fd.Key)}=@{EnvManager.Replace(fd.Value)}\"");
                        else sb.Append($" -F \"{EnvManager.Replace(fd.Key)}={EnvManager.Replace(fd.Value)}\"");
                    }
                }
            }
            return sb.ToString();
        }

        private string GenerateCSharp(RequestHistoryItem req)
        {
            string finalUrl = EnvManager.Replace(req.Url);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Net.Http;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("\nvar client = new HttpClient();");
            sb.AppendLine($"var request = new HttpRequestMessage(HttpMethod.{req.Method.Substring(0, 1).ToUpper() + req.Method.Substring(1).ToLower()}, \"{finalUrl}\");");

            // Simplified Auth headers
            if (req.AuthType == "Bearer Token") sb.AppendLine($"request.Headers.Add(\"Authorization\", \"Bearer {EnvManager.Replace(req.AuthToken)}\");");
            else if (req.AuthType == "OAuth 2.0") sb.AppendLine($"request.Headers.Add(\"Authorization\", \"Bearer {EnvManager.Replace(req.OAuth2Token)}\");");
            else if (req.AuthType == "Basic Auth") sb.AppendLine($"request.Headers.Add(\"Authorization\", \"Basic \" + Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(\"{EnvManager.Replace(req.AuthUser)}:{EnvManager.Replace(req.AuthPass)}\")));");
            else if (req.AuthType == "API Key" && req.ApiKeyAddTo == "Header") sb.AppendLine($"request.Headers.Add(\"{EnvManager.Replace(req.ApiKeyName)}\", \"{EnvManager.Replace(req.ApiKeyValue)}\");");

            foreach (var h in req.Headers) sb.AppendLine($"request.Headers.Add(\"{EnvManager.Replace(h.Key)}\", \"{EnvManager.Replace(h.Value)}\");");

            if (req.Method != "GET")
            {
                if (req.BodyType == "raw" && !string.IsNullOrWhiteSpace(req.Body))
                {
                    sb.AppendLine($"var content = new StringContent(@\"{EnvManager.Replace(req.Body).Replace("\"", "\"\"")}\", null, \"application/json\");");
                    sb.AppendLine("request.Content = content;");
                }
                else if (req.BodyType == "x-www-form-urlencoded")
                {
                    sb.AppendLine("var collection = new List<KeyValuePair<string, string>>();");
                    foreach (var kv in req.UrlEncodedData) sb.AppendLine($"collection.Add(new(\"{EnvManager.Replace(kv.Key)}\", \"{EnvManager.Replace(kv.Value)}\"));");
                    sb.AppendLine("var content = new FormUrlEncodedContent(collection);");
                    sb.AppendLine("request.Content = content;");
                }
                else if (req.BodyType == "form-data")
                {
                    sb.AppendLine("var content = new MultipartFormDataContent();");
                    foreach (var fd in req.FormData)
                    {
                        if (fd.Type == "File") sb.AppendLine($"content.Add(new StreamContent(File.OpenRead(\"{EnvManager.Replace(fd.Value).Replace("\\", "\\\\")}\")), \"{EnvManager.Replace(fd.Key)}\", \"{Path.GetFileName(EnvManager.Replace(fd.Value))}\");");
                        else sb.AppendLine($"content.Add(new StringContent(\"{EnvManager.Replace(fd.Value)}\"), \"{EnvManager.Replace(fd.Key)}\");");
                    }
                    sb.AppendLine("request.Content = content;");
                }
            }
            sb.AppendLine("var response = await client.SendAsync(request);");
            sb.AppendLine("response.EnsureSuccessStatusCode();");
            sb.AppendLine("Console.WriteLine(await response.Content.ReadAsStringAsync());");
            return sb.ToString();
        }

        private string GeneratePython(RequestHistoryItem req)
        {
            string finalUrl = EnvManager.Replace(req.Url);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("import requests\n");
            sb.AppendLine($"url = \"{finalUrl}\"");

            sb.AppendLine("\nheaders = {");
            if (req.AuthType == "Bearer Token") sb.AppendLine($"  'Authorization': 'Bearer {EnvManager.Replace(req.AuthToken)}',");
            else if (req.AuthType == "OAuth 2.0") sb.AppendLine($"  'Authorization': 'Bearer {EnvManager.Replace(req.OAuth2Token)}',");
            else if (req.AuthType == "Basic Auth") sb.AppendLine($"  'Authorization': 'Basic ' + base64.b64encode(b'{EnvManager.Replace(req.AuthUser)}:{EnvManager.Replace(req.AuthPass)}').decode('utf-8'),");
            else if (req.AuthType == "API Key" && req.ApiKeyAddTo == "Header") sb.AppendLine($"  '{EnvManager.Replace(req.ApiKeyName)}': '{EnvManager.Replace(req.ApiKeyValue)}',");
            foreach (var h in req.Headers) sb.AppendLine($"  '{EnvManager.Replace(h.Key)}': '{EnvManager.Replace(h.Value)}',");
            sb.AppendLine("}");

            string reqArgs = "url, headers=headers";

            if (req.Method != "GET")
            {
                if (req.BodyType == "raw" && !string.IsNullOrWhiteSpace(req.Body))
                {
                    sb.AppendLine($"\npayload = \"\"\"{EnvManager.Replace(req.Body)}\"\"\"");
                    reqArgs += ", data=payload";
                }
                else if (req.BodyType == "x-www-form-urlencoded")
                {
                    sb.AppendLine("\npayload = {");
                    foreach (var kv in req.UrlEncodedData) sb.AppendLine($"  '{EnvManager.Replace(kv.Key)}': '{EnvManager.Replace(kv.Value)}',");
                    sb.AppendLine("}");
                    reqArgs += ", data=payload";
                }
                else if (req.BodyType == "form-data")
                {
                    sb.AppendLine("\npayload = {}");
                    sb.AppendLine("files = []");
                    foreach (var fd in req.FormData)
                    {
                        if (fd.Type == "File") sb.AppendLine($"files.append(('{EnvManager.Replace(fd.Key)}', open('{EnvManager.Replace(fd.Value).Replace("\\", "\\\\")}', 'rb')))");
                        else sb.AppendLine($"payload['{EnvManager.Replace(fd.Key)}'] = '{EnvManager.Replace(fd.Value)}'");
                    }
                    reqArgs += ", data=payload, files=files";
                }
            }

            sb.AppendLine($"\nresponse = requests.request(\"{req.Method}\", {reqArgs})");
            sb.AppendLine("print(response.text)");
            return sb.ToString();
        }

        private string GenerateJS(RequestHistoryItem req)
        {
            string finalUrl = EnvManager.Replace(req.Url);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("const myHeaders = new Headers();");

            if (req.AuthType == "Bearer Token") sb.AppendLine($"myHeaders.append(\"Authorization\", \"Bearer {EnvManager.Replace(req.AuthToken)}\");");
            else if (req.AuthType == "OAuth 2.0") sb.AppendLine($"myHeaders.append(\"Authorization\", \"Bearer {EnvManager.Replace(req.OAuth2Token)}\");");
            else if (req.AuthType == "Basic Auth") sb.AppendLine($"myHeaders.append(\"Authorization\", \"Basic \" + btoa(\"{EnvManager.Replace(req.AuthUser)}:{EnvManager.Replace(req.AuthPass)}\"));");
            else if (req.AuthType == "API Key" && req.ApiKeyAddTo == "Header") sb.AppendLine($"myHeaders.append(\"{EnvManager.Replace(req.ApiKeyName)}\", \"{EnvManager.Replace(req.ApiKeyValue)}\");");
            foreach (var h in req.Headers) sb.AppendLine($"myHeaders.append(\"{EnvManager.Replace(h.Key)}\", \"{EnvManager.Replace(h.Value)}\");");

            sb.AppendLine("\nconst requestOptions = {");
            sb.AppendLine($"  method: '{req.Method}',");
            sb.AppendLine("  headers: myHeaders,");
            sb.AppendLine("  redirect: 'follow'");

            if (req.Method != "GET")
            {
                if (req.BodyType == "raw" && !string.IsNullOrWhiteSpace(req.Body))
                {
                    sb.Insert(0, $"const raw = JSON.stringify({EnvManager.Replace(req.Body)});\n\n");
                    sb.Insert(sb.Length - 2, ",\n  body: raw");
                }
                else if (req.BodyType == "x-www-form-urlencoded")
                {
                    sb.Insert(0, "const urlencoded = new URLSearchParams();\n");
                    foreach (var kv in req.UrlEncodedData) sb.Insert(sb.ToString().IndexOf("const requestOptions"), $"urlencoded.append(\"{EnvManager.Replace(kv.Key)}\", \"{EnvManager.Replace(kv.Value)}\");\n");
                    sb.Insert(sb.Length - 2, ",\n  body: urlencoded");
                }
                else if (req.BodyType == "form-data")
                {
                    sb.Insert(0, "const formdata = new FormData();\n");
                    foreach (var fd in req.FormData)
                    {
                        if (fd.Type == "File") sb.Insert(sb.ToString().IndexOf("const requestOptions"), $"formdata.append(\"{EnvManager.Replace(fd.Key)}\", fileInput.files[0], \"{Path.GetFileName(EnvManager.Replace(fd.Value))}\");\n");
                        else sb.Insert(sb.ToString().IndexOf("const requestOptions"), $"formdata.append(\"{EnvManager.Replace(fd.Key)}\", \"{EnvManager.Replace(fd.Value)}\");\n");
                    }
                    sb.Insert(sb.Length - 2, ",\n  body: formdata");
                }
            }
            sb.AppendLine("};\n");

            sb.AppendLine($"fetch(\"{finalUrl}\", requestOptions)");
            sb.AppendLine("  .then(response => response.text())");
            sb.AppendLine("  .then(result => console.log(result))");
            sb.AppendLine("  .catch(error => console.log('error', error));");
            return sb.ToString();
        }

        private string GenerateGo(RequestHistoryItem req)
        {
            string finalUrl = EnvManager.Replace(req.Url);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("package main\n\nimport (\n  \"fmt\"\n  \"net/http\"\n  \"io/ioutil\"\n  \"strings\"\n)\n");
            sb.AppendLine("func main() {");
            sb.AppendLine($"  url := \"{finalUrl}\"\n  method := \"{req.Method}\"\n");

            if (req.Method != "GET")
            {
                if (req.BodyType == "raw" && !string.IsNullOrWhiteSpace(req.Body))
                {
                    sb.AppendLine($"  payload := strings.NewReader(`{EnvManager.Replace(req.Body)}`)");
                    sb.AppendLine("  req, err := http.NewRequest(method, url, payload)");
                }
                else if (req.BodyType == "x-www-form-urlencoded")
                {
                    List<string> pairs = new List<string>();
                    foreach (var kv in req.UrlEncodedData) pairs.Add($"{EnvManager.Replace(kv.Key)}={EnvManager.Replace(kv.Value)}");
                    sb.AppendLine($"  payload := strings.NewReader(\"{string.Join("&", pairs)}\")");
                    sb.AppendLine("  req, err := http.NewRequest(method, url, payload)");
                }
                else if (req.BodyType == "form-data")
                {
                    sb.AppendLine("  // Note: Go requires 'mime/multipart' to natively construct Form-Data boundaries.");
                    sb.AppendLine("  // For complete code, use Postman's native export or write a multipart.Writer wrapper.");
                    sb.AppendLine("  req, err := http.NewRequest(method, url, nil)");
                }
            }
            else
            {
                sb.AppendLine("  req, err := http.NewRequest(method, url, nil)");
            }

            sb.AppendLine("  if err != nil { fmt.Println(err); return }");

            if (req.AuthType == "Bearer Token") sb.AppendLine($"  req.Header.Add(\"Authorization\", \"Bearer {EnvManager.Replace(req.AuthToken)}\")");
            else if (req.AuthType == "OAuth 2.0") sb.AppendLine($"  req.Header.Add(\"Authorization\", \"Bearer {EnvManager.Replace(req.OAuth2Token)}\")");
            else if (req.AuthType == "Basic Auth") sb.AppendLine($"  req.SetBasicAuth(\"{EnvManager.Replace(req.AuthUser)}\", \"{EnvManager.Replace(req.AuthPass)}\")");
            else if (req.AuthType == "API Key" && req.ApiKeyAddTo == "Header") sb.AppendLine($"  req.Header.Add(\"{EnvManager.Replace(req.ApiKeyName)}\", \"{EnvManager.Replace(req.ApiKeyValue)}\")");

            foreach (var h in req.Headers) sb.AppendLine($"  req.Header.Add(\"{EnvManager.Replace(h.Key)}\", \"{EnvManager.Replace(h.Value)}\")");

            sb.AppendLine("\n  client := &http.Client { }");
            sb.AppendLine("  res, err := client.Do(req)");
            sb.AppendLine("  if err != nil { fmt.Println(err); return }\n  defer res.Body.Close()");
            sb.AppendLine("  body, err := ioutil.ReadAll(res.Body)\n  if err != nil { fmt.Println(err); return }");
            sb.AppendLine("  fmt.Println(string(body))\n}");
            return sb.ToString();
        }

        private void ShowImportCurlDialog()
        {
            Form prompt = new Form() { Width = 600, Height = 400, FormBorderStyle = FormBorderStyle.FixedDialog, Text = "Import cURL", StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
            Label lbl = new Label() { Left = 10, Top = 10, Width = 560, Text = "Paste your cURL command below:" };
            TextBox txtCurl = new TextBox() { Left = 10, Top = 30, Width = 560, Height = 280, Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9) };
            Button btnImport = new Button() { Text = "Import", Left = 490, Top = 320, Width = 80, DialogResult = DialogResult.OK };
            Button btnCancel = new Button() { Text = "Cancel", Left = 400, Top = 320, Width = 80, DialogResult = DialogResult.Cancel };
            prompt.Controls.Add(lbl); prompt.Controls.Add(txtCurl); prompt.Controls.Add(btnImport); prompt.Controls.Add(btnCancel);
            prompt.AcceptButton = btnImport; prompt.CancelButton = btnCancel;

            if (prompt.ShowDialog(this) == DialogResult.OK) ApplyCurl(txtCurl.Text);
        }

        private void ApplyCurl(string curlCommand)
        {
            if (string.IsNullOrWhiteSpace(curlCommand)) return;
            var args = ParseArguments(curlCommand);
            if (args.Count == 0 || !args[0].Equals("curl", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("Invalid cURL command."); return; }

            string method = "GET", url = "", body = "";
            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();

            for (int i = 1; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg.Equals("-X") || arg.Equals("--request")) { if (i + 1 < args.Count) method = args[++i].ToUpper(); }
                else if (arg.Equals("-H") || arg.Equals("--header"))
                {
                    if (i + 1 < args.Count) { string headerStr = args[++i]; int colonIndex = headerStr.IndexOf(':'); if (colonIndex > 0) headers.Add(new KeyValuePair<string, string>(headerStr.Substring(0, colonIndex).Trim(), headerStr.Substring(colonIndex + 1).Trim())); }
                }
                else if (arg.Equals("-d") || arg.Equals("--data") || arg.Equals("--data-raw") || arg.Equals("--data-binary") || arg.Equals("--data-urlencode"))
                {
                    if (i + 1 < args.Count) { body = args[++i]; if (method == "GET") method = "POST"; }
                }
                else if (arg.Equals("-b") || arg.Equals("--cookie") || arg.Equals("-A") || arg.Equals("--user-agent") || arg.Equals("-u") || arg.Equals("--user")) i++;
                else if (!arg.StartsWith("-")) url = arg;
            }

            if (cmbMethod.Items.Contains(method)) cmbMethod.SelectedItem = method; else cmbMethod.Text = method;
            txtUrl.Text = url; dgvHeaders.Rows.Clear();
            foreach (var h in headers) dgvHeaders.Rows.Add(h.Key, h.Value);

            rbBodyRaw.Checked = true;
            txtRequestBody.Text = body;

            if (rbBodyRaw.Checked && !string.IsNullOrWhiteSpace(txtRequestBody.Text))
            {
                try { txtRequestBody.Rtf = GenerateRtfFromJson(TryFormatJson(txtRequestBody.Text)); } catch { }
            }
        }

        private static List<string> ParseArguments(string commandLine)
        {
            var args = new List<string>(); bool inQuotes = false; char quoteChar = '\0'; var currentArg = new StringBuilder();
            commandLine = commandLine.Replace("\\\n", " ").Replace("\\\r\n", " ");
            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (inQuotes)
                {
                    if (c == '\\' && i + 1 < commandLine.Length) { currentArg.Append(commandLine[i + 1]); i++; }
                    else if (c == quoteChar) inQuotes = false;
                    else currentArg.Append(c);
                }
                else
                {
                    if (c == '\'' || c == '"') { inQuotes = true; quoteChar = c; }
                    else if (char.IsWhiteSpace(c)) { if (currentArg.Length > 0) { args.Add(currentArg.ToString()); currentArg.Clear(); } }
                    else currentArg.Append(c);
                }
            }
            if (currentArg.Length > 0) args.Add(currentArg.ToString());
            return args;
        }

        private async Task PerformOAuth2FlowAsync()
        {
            string authUrl = EnvManager.Replace(txtOAuth2AuthUrl.Text);
            string tokenUrl = EnvManager.Replace(txtOAuth2TokenUrl.Text);
            string clientId = EnvManager.Replace(txtOAuth2ClientId.Text);
            string clientSecret = EnvManager.Replace(txtOAuth2ClientSecret.Text);
            string scope = EnvManager.Replace(txtOAuth2Scope.Text);
            string redirectUri = EnvManager.Replace(txtOAuth2RedirectUri.Text);

            if (string.IsNullOrWhiteSpace(redirectUri)) redirectUri = "http://localhost:8080/";

            string state = Guid.NewGuid().ToString("N");
            string finalAuthUrl = $"{authUrl}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={state}";
            if (!string.IsNullOrWhiteSpace(scope)) finalAuthUrl += $"&scope={Uri.EscapeDataString(scope)}";

            using (var listener = new HttpListener())
            {
                try
                {
                    listener.Prefixes.Add(redirectUri.EndsWith("/") ? redirectUri : redirectUri + "/");
                    listener.Start();

                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = finalAuthUrl, UseShellExecute = true });
                    }
                    catch
                    {
                        System.Diagnostics.Process.Start(finalAuthUrl);
                    }

                    lblStatusBadge.Visible = false;
                    lblStatusText.Text = "Waiting for OAuth2 browser callback...";
                    lblStatusText.ForeColor = Color.DarkOrange;

                    var context = await listener.GetContextAsync();
                    var request = context.Request;

                    string code = request.QueryString["code"];
                    string returnedState = request.QueryString["state"];

                    string responseString = "<html><head><style>body{font-family:sans-serif;text-align:center;padding:50px;}</style></head><body><h2>Authentication successful!</h2><p>You can close this window and return to the API tester.</p><script>window.close();</script></body></html>";
                    byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    context.Response.OutputStream.Close();

                    if (string.IsNullOrEmpty(code))
                    {
                        MessageBox.Show("Authorization failed or was denied.", "OAuth2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        lblStatusText.Text = "OAuth2 failed.";
                        lblStatusText.ForeColor = Color.Black;
                        return;
                    }

                    lblStatusText.Text = "Exchanging code for token...";

                    var client = SimpleHttpClientFactory.GetClient();
                    var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
                    var formData = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("grant_type", "authorization_code"),
                        new KeyValuePair<string, string>("code", code),
                        new KeyValuePair<string, string>("redirect_uri", redirectUri),
                        new KeyValuePair<string, string>("client_id", clientId),
                    };
                    if (!string.IsNullOrEmpty(clientSecret)) formData.Add(new KeyValuePair<string, string>("client_secret", clientSecret));

                    tokenRequest.Content = new FormUrlEncodedContent(formData);
                    var tokenResponse = await client.SendAsync(tokenRequest);
                    string json = await tokenResponse.Content.ReadAsStringAsync();

                    if (tokenResponse.IsSuccessStatusCode)
                    {
                        try
                        {
                            using (JsonDocument doc = JsonDocument.Parse(json))
                            {
                                if (doc.RootElement.TryGetProperty("access_token", out var accToken))
                                {
                                    txtOAuth2Token.Text = accToken.GetString();
                                    MessageBox.Show("Access Token retrieved successfully!", "OAuth2", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                }
                            }
                        }
                        catch { MessageBox.Show("Failed to parse token response.", "OAuth2 Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    }
                    else
                    {
                        MessageBox.Show($"Failed to retrieve token: {tokenResponse.StatusCode}\n{json}", "OAuth2 Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    lblStatusText.Text = "Waiting for request...";
                    lblStatusText.ForeColor = Color.Black;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"OAuth2 error: {ex.Message}", "OAuth2 Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    lblStatusText.Text = "OAuth2 failed.";
                    lblStatusText.ForeColor = Color.Black;
                }
                finally
                {
                    if (listener.IsListening) listener.Stop();
                }
            }
        }

        private async Task HandleSendClickAsync()
        {
            if (btnSend.Text == "Cancel") { _cts?.Cancel(); return; }
            if (string.IsNullOrWhiteSpace(txtUrl.Text)) return;

            btnSend.Text = "Cancel"; btnSend.BackColor = Color.IndianRed;
            btnClear.Enabled = false; btnImportCurl.Enabled = false; btnCodeSnippet.Enabled = false;

            txtResponse.Text = "Sending request..."; dgvResponseHeaders.Rows.Clear();
            lblStatusBadge.Visible = false;
            lblStatusText.Text = "Pending..."; lblStatusText.ForeColor = Color.Black;

            _cts = new CancellationTokenSource();

            try
            {
                var historyItem = GetCurrentRequestSnapshot();

                string finalUrl = EnvManager.Replace(txtUrl.Text);

                if (historyItem.AuthType == "API Key" && !string.IsNullOrWhiteSpace(historyItem.ApiKeyName) && historyItem.ApiKeyAddTo == "Query Params")
                {
                    string delimiter = finalUrl.Contains("?") ? "&" : "?";
                    finalUrl = $"{finalUrl}{delimiter}{Uri.EscapeDataString(EnvManager.Replace(historyItem.ApiKeyName))}={Uri.EscapeDataString(EnvManager.Replace(historyItem.ApiKeyValue))}";
                }

                var method = new HttpMethod(historyItem.Method);
                var request = new HttpRequestMessage(method, finalUrl);

                if (historyItem.AuthType == "Bearer Token" && !string.IsNullOrWhiteSpace(historyItem.AuthToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EnvManager.Replace(historyItem.AuthToken));
                else if (historyItem.AuthType == "OAuth 2.0" && !string.IsNullOrWhiteSpace(historyItem.OAuth2Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EnvManager.Replace(historyItem.OAuth2Token));
                else if (historyItem.AuthType == "Basic Auth" && (!string.IsNullOrEmpty(historyItem.AuthUser) || !string.IsNullOrEmpty(historyItem.AuthPass)))
                {
                    var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{EnvManager.Replace(historyItem.AuthUser)}:{EnvManager.Replace(historyItem.AuthPass)}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
                }
                else if (historyItem.AuthType == "API Key" && !string.IsNullOrWhiteSpace(historyItem.ApiKeyName) && historyItem.ApiKeyAddTo == "Header")
                {
                    request.Headers.TryAddWithoutValidation(EnvManager.Replace(historyItem.ApiKeyName), EnvManager.Replace(historyItem.ApiKeyValue));
                }

                foreach (var h in historyItem.Headers)
                {
                    if (!h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation(EnvManager.Replace(h.Key), EnvManager.Replace(h.Value));
                }

                OnRequestSent?.Invoke(historyItem);

                // --- BUILD ADVANCED BODY CONTENT ---
                if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch)
                {
                    if (historyItem.BodyType == "raw" && !string.IsNullOrWhiteSpace(historyItem.Body))
                    {
                        request.Content = new StringContent(EnvManager.Replace(historyItem.Body), Encoding.UTF8, "application/json");
                    }
                    else if (historyItem.BodyType == "form-data")
                    {
                        var multipart = new MultipartFormDataContent();
                        foreach (var fd in historyItem.FormData)
                        {
                            string key = EnvManager.Replace(fd.Key);
                            string val = EnvManager.Replace(fd.Value);
                            if (fd.Type == "File" && File.Exists(val))
                            {
                                var fileContent = new ByteArrayContent(File.ReadAllBytes(val));
                                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                                multipart.Add(fileContent, key, Path.GetFileName(val));
                            }
                            else
                            {
                                multipart.Add(new StringContent(val), key);
                            }
                        }
                        request.Content = multipart;
                    }
                    else if (historyItem.BodyType == "x-www-form-urlencoded")
                    {
                        var kvList = new List<KeyValuePair<string, string>>();
                        foreach (var kv in historyItem.UrlEncodedData)
                        {
                            kvList.Add(new KeyValuePair<string, string>(EnvManager.Replace(kv.Key), EnvManager.Replace(kv.Value)));
                        }
                        request.Content = new FormUrlEncodedContent(kvList);
                    }
                }

                DateTime startTime = DateTime.Now;

                // Fetch instance safely via Factory to avoid DNS staleness
                var client = SimpleHttpClientFactory.GetClient();

                var response = await client.SendAsync(request, _cts.Token);
                var duration = DateTime.Now - startTime;

                foreach (var header in response.Headers) dgvResponseHeaders.Rows.Add(header.Key, string.Join(", ", header.Value));
                if (response.Content?.Headers != null) foreach (var header in response.Content.Headers) dgvResponseHeaders.Rows.Add(header.Key, string.Join(", ", header.Value));

                string content = await response.Content.ReadAsStringAsync();

                // UI Badge
                int code = (int)response.StatusCode;
                lblStatusBadge.Text = code.ToString();
                if (code >= 200 && code < 300) lblStatusBadge.BackColor = Color.FromArgb(40, 167, 69);
                else if (code >= 300 && code < 400) lblStatusBadge.BackColor = Color.FromArgb(255, 193, 7);
                else if (code >= 400 && code < 500) lblStatusBadge.BackColor = Color.FromArgb(253, 126, 20);
                else if (code >= 500) lblStatusBadge.BackColor = Color.FromArgb(220, 53, 69);
                else lblStatusBadge.BackColor = Color.Gray;
                lblStatusBadge.Visible = true;

                lblStatusText.Text = $"{response.ReasonPhrase}  |  Time: {duration.TotalMilliseconds:F0} ms";
                lblStatusText.ForeColor = Color.Black;

                string mediaType = response.Content.Headers.ContentType?.MediaType?.ToLower() ?? "";
                if (mediaType.Contains("json"))
                {
                    string formattedJson = TryFormatJson(content);
                    txtResponse.Rtf = GenerateRtfFromJson(formattedJson);
                }
                else if (mediaType.Contains("xml")) txtResponse.Text = TryFormatXml(content);
                else if (mediaType.Contains("html")) txtResponse.Text = TryFormatHtml(content);
                else txtResponse.Text = content;
            }
            catch (OperationCanceledException)
            {
                lblStatusBadge.Visible = false;
                lblStatusText.Text = "Cancelled"; lblStatusText.ForeColor = Color.DarkOrange;
                txtResponse.Text = "Request was cancelled by the user.";
            }
            catch (Exception ex)
            {
                lblStatusBadge.Visible = false;
                lblStatusText.Text = "Request Error"; lblStatusText.ForeColor = Color.Red;
                txtResponse.Text = ex.Message;
            }
            finally
            {
                btnSend.Text = "Send"; btnSend.BackColor = Color.DodgerBlue;
                btnClear.Enabled = true; btnImportCurl.Enabled = true; btnCodeSnippet.Enabled = true;
                _cts?.Dispose(); _cts = null;
            }
        }

        // Fast Custom Syntax Highlighter generating an RTF string natively
        private string GenerateRtfFromJson(string json)
        {
            StringBuilder sb = new StringBuilder();

            // Light theme colors: 0: fg(Black) 1: String(Brown) 2: Key(Blue) 3: Num(Green) 4: Bool(Blue)
            sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Consolas;}}{\colortbl;\red0\green0\blue0;\red163\green21\blue21;\red4\green81\blue165;\red9\green134\blue88;\red0\green0\blue255;}\cf0 ");

            string pattern = @"(""(?:[^""\\]|\\.)*"")(\s*:)|(""(?:[^""\\]|\\.)*"")|([-+]?\d*\.\d+|\b[-+]?\d+\b)|\b(true|false|null)\b";

            int lastIndex = 0;
            foreach (Match m in Regex.Matches(json, pattern, RegexOptions.IgnoreCase))
            {
                if (m.Index > lastIndex) sb.Append(EscapeRtf(json.Substring(lastIndex, m.Index - lastIndex)));

                if (m.Groups[1].Success) sb.Append(@"\cf2 ").Append(EscapeRtf(m.Groups[1].Value)).Append(@"\cf0 ").Append(EscapeRtf(m.Groups[2].Value));
                else if (m.Groups[3].Success) sb.Append(@"\cf1 ").Append(EscapeRtf(m.Groups[3].Value)).Append(@"\cf0 ");
                else if (m.Groups[4].Success) sb.Append(@"\cf3 ").Append(EscapeRtf(m.Groups[4].Value)).Append(@"\cf0 ");
                else if (m.Groups[5].Success) sb.Append(@"\cf4 ").Append(EscapeRtf(m.Groups[5].Value)).Append(@"\cf0 ");

                lastIndex = m.Index + m.Length;
            }

            if (lastIndex < json.Length) sb.Append(EscapeRtf(json.Substring(lastIndex)));
            sb.Append("}");
            return sb.ToString();
        }

        private string EscapeRtf(string text)
        {
            text = text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
            text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\par ");
            return text;
        }

        private string TryFormatHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return html;
            try
            {
                html = Regex.Replace(html.Replace("\r", "").Replace("\n", ""), @"(>)\s*(<)", "$1\r\n$2");
                var lines = html.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                var sb = new StringBuilder();
                int indent = 0;
                string[] voidTags = { "<area", "<base", "<br", "<col", "<embed", "<hr", "<img", "<input", "<link", "<meta", "<param", "<source", "<track", "<wbr", "<!doctype", "<?" };

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;
                    if (trimmed.StartsWith("</")) indent = Math.Max(0, indent - 1);
                    sb.AppendLine(new string(' ', indent * 2) + trimmed);

                    if (trimmed.StartsWith("<") && !trimmed.StartsWith("</") && !trimmed.EndsWith("/>"))
                    {
                        bool isVoidTag = false;
                        foreach (var vTag in voidTags) { if (trimmed.StartsWith(vTag, StringComparison.OrdinalIgnoreCase)) { isVoidTag = true; break; } }
                        bool isInlineClosed = Regex.IsMatch(trimmed, @"<([^>\s]+)[^>]*>.*</\1>");
                        if (!isVoidTag && !isInlineClosed) indent++;
                    }
                }
                return sb.ToString();
            }
            catch { return html; }
        }

        private string TryFormatXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return xml;
            try { return XDocument.Parse(xml).ToString(); } catch { return xml; }
        }

        private string TryFormatJson(string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return str;
            str = str.Trim();
            if ((str.StartsWith("{") && str.EndsWith("}")) || (str.StartsWith("[") && str.EndsWith("]")))
            {
                try
                {
                    var indent = 0; var quoted = false; var sb = new StringBuilder();
                    for (var i = 0; i < str.Length; i++)
                    {
                        var ch = str[i];
                        switch (ch)
                        {
                            case '{': case '[': sb.Append(ch); if (!quoted) { sb.AppendLine(); Enumerable.Range(0, ++indent).ToList().ForEach(item => sb.Append("  ")); } break;
                            case '}': case ']': if (!quoted) { sb.AppendLine(); Enumerable.Range(0, --indent).ToList().ForEach(item => sb.Append("  ")); } sb.Append(ch); break;
                            case '"': sb.Append(ch); bool escaped = false; var index = i; while (index > 0 && str[--index] == '\\') escaped = !escaped; if (!escaped) quoted = !quoted; break;
                            case ',': sb.Append(ch); if (!quoted) { sb.AppendLine(); Enumerable.Range(0, indent).ToList().ForEach(item => sb.Append("  ")); } break;
                            case ':': sb.Append(ch); if (!quoted) sb.Append(" "); break;
                            default: if (!quoted && char.IsWhiteSpace(ch)) { break; } sb.Append(ch); break;
                        }
                    }
                    return sb.ToString();
                }
                catch { return str; }
            }
            return str;
        }

        public void ClearRequest()
        {
            txtUrl.Text = ""; cmbMethod.SelectedIndex = 0; dgvParams.Rows.Clear(); dgvHeaders.Rows.Clear(); txtRequestBody.Text = ""; cmbAuthType.SelectedIndex = 0;
            rbBodyRaw.Checked = true; dgvFormData.Rows.Clear(); dgvUrlEncoded.Rows.Clear();
            txtApiKeyName.Text = "api_key"; txtApiKeyValue.Text = ""; if (cmbApiKeyAddTo.Items.Count > 0) cmbApiKeyAddTo.SelectedIndex = 0;

            txtBearerToken.Text = ""; txtBasicUser.Text = ""; txtBasicPass.Text = "";
            txtOAuth2AuthUrl.Text = ""; txtOAuth2TokenUrl.Text = ""; txtOAuth2ClientId.Text = ""; txtOAuth2ClientSecret.Text = ""; txtOAuth2Scope.Text = ""; txtOAuth2RedirectUri.Text = "http://localhost:8080/"; txtOAuth2Token.Text = "";

            txtResponse.Text = ""; dgvResponseHeaders.Rows.Clear();
            lblStatusBadge.Visible = false; lblStatusText.Text = "Waiting for request..."; lblStatusText.ForeColor = Color.Black; HideSearch();
        }

        public void LoadFromHistory(RequestHistoryItem item)
        {
            ClearRequest();
            cmbMethod.SelectedItem = item.Method; txtUrl.Text = item.Url; txtRequestBody.Text = item.Body;

            if (item.BodyType == "form-data") { rbBodyFormData.Checked = true; foreach (var fd in item.FormData) dgvFormData.Rows.Add(fd.Key, fd.Type, fd.Value); }
            else if (item.BodyType == "x-www-form-urlencoded") { rbBodyUrlEncoded.Checked = true; foreach (var urlData in item.UrlEncodedData) dgvUrlEncoded.Rows.Add(urlData.Key, urlData.Value); }
            else rbBodyRaw.Checked = true;

            if (cmbAuthType.Items.Contains(item.AuthType)) cmbAuthType.SelectedItem = item.AuthType; else cmbAuthType.SelectedIndex = 0;
            txtApiKeyName.Text = item.ApiKeyName ?? "api_key"; txtApiKeyValue.Text = item.ApiKeyValue ?? "";
            if (!string.IsNullOrWhiteSpace(item.ApiKeyAddTo) && cmbApiKeyAddTo.Items.Contains(item.ApiKeyAddTo)) cmbApiKeyAddTo.SelectedItem = item.ApiKeyAddTo;

            txtBearerToken.Text = item.AuthToken; txtBasicUser.Text = item.AuthUser; txtBasicPass.Text = item.AuthPass;
            txtOAuth2AuthUrl.Text = item.OAuth2AuthUrl ?? ""; txtOAuth2TokenUrl.Text = item.OAuth2TokenUrl ?? ""; txtOAuth2ClientId.Text = item.OAuth2ClientId ?? "";
            txtOAuth2ClientSecret.Text = item.OAuth2ClientSecret ?? ""; txtOAuth2Scope.Text = item.OAuth2Scope ?? ""; txtOAuth2RedirectUri.Text = item.OAuth2RedirectUri ?? "http://localhost:8080/"; txtOAuth2Token.Text = item.OAuth2Token ?? "";

            foreach (var h in item.Headers) dgvHeaders.Rows.Add(h.Key, h.Value);

            if (rbBodyRaw.Checked && !string.IsNullOrWhiteSpace(txtRequestBody.Text))
            {
                try { txtRequestBody.Rtf = GenerateRtfFromJson(TryFormatJson(txtRequestBody.Text)); } catch { }
            }
        }

        private void SyncUrlToParams()
        {
            if (isSyncingParams) return;
            isSyncingParams = true;
            try
            {
                dgvParams.Rows.Clear(); string url = txtUrl.Text; int qIndex = url.IndexOf('?');
                if (qIndex >= 0 && qIndex < url.Length - 1)
                {
                    var pairs = url.Substring(qIndex + 1).Split('&');
                    foreach (var pair in pairs)
                    {
                        var kv = pair.Split('='); string key = Uri.UnescapeDataString(kv[0]); string val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
                        if (!string.IsNullOrEmpty(key)) dgvParams.Rows.Add(key, val);
                    }
                }
            }
            catch { /* Ignore parsing errors while typing */ }
            finally { isSyncingParams = false; }
        }

        private void SyncParamsToUrl()
        {
            if (isSyncingParams) return;
            isSyncingParams = true;
            try
            {
                string url = txtUrl.Text; int qIndex = url.IndexOf('?'); string baseUrl = qIndex >= 0 ? url.Substring(0, qIndex) : url;
                List<string> qParams = new List<string>();
                foreach (DataGridViewRow row in dgvParams.Rows)
                {
                    if (!row.IsNewRow)
                    {
                        string key = row.Cells[0].Value?.ToString() ?? ""; string val = row.Cells[1].Value?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(key)) qParams.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(val)}");
                    }
                }
                txtUrl.Text = qParams.Count > 0 ? baseUrl + "?" + string.Join("&", qParams) : baseUrl;
            }
            catch { /* Ignore errors */ }
            finally { isSyncingParams = false; }
        }

        private void SetupAuthTab()
        {
            Label lblAuthType = new Label { Text = "Type:", Left = 10, Top = 15, Width = 40 };
            cmbAuthType = new ComboBox { Left = 50, Top = 12, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAuthType.Items.AddRange(new string[] { "No Auth", "API Key", "Bearer Token", "Basic Auth", "OAuth 2.0" });

            pnlAuthContent = new Panel { Left = 10, Top = 50, Width = 700, Height = 130 };

            lblApiKeyName = new Label { Text = "Key:", Left = 0, Top = 5, Width = 50, Visible = false }; txtApiKeyName = new TextBox { Left = 50, Top = 2, Width = 200, Visible = false, Text = "api_key" };
            lblApiKeyValue = new Label { Text = "Value:", Left = 0, Top = 35, Width = 50, Visible = false }; txtApiKeyValue = new TextBox { Left = 50, Top = 32, Width = 400, Visible = false };
            lblApiKeyAddTo = new Label { Text = "Add to:", Left = 0, Top = 65, Width = 50, Visible = false }; cmbApiKeyAddTo = new ComboBox { Left = 50, Top = 62, Width = 150, Visible = false, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbApiKeyAddTo.Items.AddRange(new string[] { "Header", "Query Params" }); cmbApiKeyAddTo.SelectedIndex = 0;

            lblBearerToken = new Label { Text = "Token:", Left = 0, Top = 5, Width = 50, Visible = false }; txtBearerToken = new TextBox { Left = 50, Top = 2, Width = 400, Visible = false };
            lblBasicUser = new Label { Text = "Username:", Left = 0, Top = 5, Width = 70, Visible = false }; txtBasicUser = new TextBox { Left = 75, Top = 2, Width = 250, Visible = false };
            lblBasicPass = new Label { Text = "Password:", Left = 0, Top = 35, Width = 70, Visible = false }; txtBasicPass = new TextBox { Left = 75, Top = 32, Width = 250, Visible = false, UseSystemPasswordChar = true };

            // OAuth 2.0 Inputs
            lblOAuth2AuthUrl = new Label { Text = "Auth URL:", Left = 0, Top = 5, Width = 80, Visible = false };
            txtOAuth2AuthUrl = new TextBox { Left = 85, Top = 2, Width = 250, Visible = false };

            lblOAuth2TokenUrl = new Label { Text = "Token URL:", Left = 350, Top = 5, Width = 80, Visible = false };
            txtOAuth2TokenUrl = new TextBox { Left = 435, Top = 2, Width = 250, Visible = false };

            lblOAuth2ClientId = new Label { Text = "Client ID:", Left = 0, Top = 35, Width = 80, Visible = false };
            txtOAuth2ClientId = new TextBox { Left = 85, Top = 32, Width = 250, Visible = false };

            lblOAuth2ClientSecret = new Label { Text = "Client Secret:", Left = 350, Top = 35, Width = 80, Visible = false };
            txtOAuth2ClientSecret = new TextBox { Left = 435, Top = 32, Width = 250, Visible = false, UseSystemPasswordChar = true };

            lblOAuth2Scope = new Label { Text = "Scope:", Left = 0, Top = 65, Width = 80, Visible = false };
            txtOAuth2Scope = new TextBox { Left = 85, Top = 62, Width = 250, Visible = false };

            lblOAuth2RedirectUri = new Label { Text = "Redirect URI:", Left = 350, Top = 65, Width = 80, Visible = false };
            txtOAuth2RedirectUri = new TextBox { Left = 435, Top = 62, Width = 250, Visible = false, Text = "http://localhost:8080/" };

            btnGetOAuth2Token = new Button { Text = "Get Token", Left = 85, Top = 92, Width = 100, Height = 25, Visible = false, FlatStyle = FlatStyle.Flat, BackColor = Color.DodgerBlue, ForeColor = Color.White };
            btnGetOAuth2Token.Click += async (s, e) => {
                try
                {
                    btnGetOAuth2Token.Enabled = false; btnGetOAuth2Token.Text = "Waiting...";
                    await PerformOAuth2FlowAsync();
                }
                finally
                {
                    btnGetOAuth2Token.Enabled = true; btnGetOAuth2Token.Text = "Get Token";
                }
            };

            lblOAuth2Token = new Label { Text = "Access Token:", Left = 350, Top = 95, Width = 80, Visible = false };
            txtOAuth2Token = new TextBox { Left = 435, Top = 92, Width = 250, Visible = false };

            pnlAuthContent.Controls.Add(lblApiKeyName); pnlAuthContent.Controls.Add(txtApiKeyName); pnlAuthContent.Controls.Add(lblApiKeyValue); pnlAuthContent.Controls.Add(txtApiKeyValue); pnlAuthContent.Controls.Add(lblApiKeyAddTo); pnlAuthContent.Controls.Add(cmbApiKeyAddTo);
            pnlAuthContent.Controls.Add(lblBearerToken); pnlAuthContent.Controls.Add(txtBearerToken); pnlAuthContent.Controls.Add(lblBasicUser); pnlAuthContent.Controls.Add(txtBasicUser); pnlAuthContent.Controls.Add(lblBasicPass); pnlAuthContent.Controls.Add(txtBasicPass);
            pnlAuthContent.Controls.Add(lblOAuth2AuthUrl); pnlAuthContent.Controls.Add(txtOAuth2AuthUrl); pnlAuthContent.Controls.Add(lblOAuth2TokenUrl); pnlAuthContent.Controls.Add(txtOAuth2TokenUrl); pnlAuthContent.Controls.Add(lblOAuth2ClientId); pnlAuthContent.Controls.Add(txtOAuth2ClientId); pnlAuthContent.Controls.Add(lblOAuth2ClientSecret); pnlAuthContent.Controls.Add(txtOAuth2ClientSecret); pnlAuthContent.Controls.Add(lblOAuth2Scope); pnlAuthContent.Controls.Add(txtOAuth2Scope); pnlAuthContent.Controls.Add(lblOAuth2RedirectUri); pnlAuthContent.Controls.Add(txtOAuth2RedirectUri); pnlAuthContent.Controls.Add(btnGetOAuth2Token); pnlAuthContent.Controls.Add(lblOAuth2Token); pnlAuthContent.Controls.Add(txtOAuth2Token);

            tabAuth.Controls.Add(lblAuthType); tabAuth.Controls.Add(cmbAuthType); tabAuth.Controls.Add(pnlAuthContent);

            cmbAuthType.SelectedIndexChanged += (s, e) => {
                string type = cmbAuthType.SelectedItem.ToString();
                bool isApiKey = type == "API Key", isBearer = type == "Bearer Token", isBasic = type == "Basic Auth", isOAuth2 = type == "OAuth 2.0";

                lblApiKeyName.Visible = isApiKey; txtApiKeyName.Visible = isApiKey; lblApiKeyValue.Visible = isApiKey; txtApiKeyValue.Visible = isApiKey; lblApiKeyAddTo.Visible = isApiKey; cmbApiKeyAddTo.Visible = isApiKey;
                lblBearerToken.Visible = isBearer; txtBearerToken.Visible = isBearer;
                lblBasicUser.Visible = isBasic; txtBasicUser.Visible = isBasic; lblBasicPass.Visible = isBasic; txtBasicPass.Visible = isBasic;
                lblOAuth2AuthUrl.Visible = txtOAuth2AuthUrl.Visible = isOAuth2; lblOAuth2TokenUrl.Visible = txtOAuth2TokenUrl.Visible = isOAuth2;
                lblOAuth2ClientId.Visible = txtOAuth2ClientId.Visible = isOAuth2; lblOAuth2ClientSecret.Visible = txtOAuth2ClientSecret.Visible = isOAuth2;
                lblOAuth2Scope.Visible = txtOAuth2Scope.Visible = isOAuth2; lblOAuth2RedirectUri.Visible = txtOAuth2RedirectUri.Visible = isOAuth2;
                btnGetOAuth2Token.Visible = isOAuth2; lblOAuth2Token.Visible = txtOAuth2Token.Visible = isOAuth2;
            };
            cmbAuthType.SelectedIndex = 0;
        }
    }

    // --- Standard application entry point ---
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ApiTesterForm());
        }
    }
}