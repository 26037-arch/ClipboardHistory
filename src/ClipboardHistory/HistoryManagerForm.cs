using System.Drawing;

namespace ClipboardHistory;

internal sealed class HistoryManagerForm : Form
{
    private readonly ClipboardHistoryRepository _repository;
    private readonly DatabaseWriter _writer;
    private readonly AppSettingsRepository _settingsRepository;
    private readonly StorageUsageService _storageUsage;
    private readonly HistoryRetentionService _retentionService;
    private readonly ClipboardRestoreService _restoreService;
    private readonly ClipboardImageStore _imageStore;
    private readonly AppLogger _logger;
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly Label _usageLabel = new();
    private readonly TreeView _dateTree = new();
    private readonly ListView _recordList = new();
    private readonly Button _loadMoreButton = new();
    private readonly ListView _trashList = new();
    private readonly CheckBox _automaticDeletion = new();
    private readonly ComboBox _retentionPeriod = new();
    private readonly Label _settingsStatus = new();
    private readonly TabControl _tabs = new();
    private readonly ImageList _recordThumbnails = CreateThumbnailList();
    private readonly ImageList _trashThumbnails = CreateThumbnailList();
    private readonly HashSet<long> _thumbnailLoads = [];

    private HistoryDateRange? _selectedRange;
    private int _recordOffset;
    private long _treeGeneration;
    private long _recordGeneration;
    private long _trashGeneration;
    private long _usageGeneration;
    private bool _loadingSettings;
    private bool _disposedResources;

    public HistoryManagerForm(
        ClipboardHistoryRepository repository,
        DatabaseWriter writer,
        AppSettingsRepository settingsRepository,
        StorageUsageService storageUsage,
        HistoryRetentionService retentionService,
        ClipboardRestoreService restoreService,
        ClipboardImageStore imageStore,
        AppLogger logger)
    {
        _repository = repository;
        _writer = writer;
        _settingsRepository = settingsRepository;
        _storageUsage = storageUsage;
        _retentionService = retentionService;
        _restoreService = restoreService;
        _imageStore = imageStore;
        _logger = logger;

        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(980, 680);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Text = "전체 기록";
        ShowIcon = false;
        ShowInTaskbar = true;
        TopMost = false;

        Controls.Add(CreateLayout());
        Shown += (_, _) => InitializeAsync();
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == 1)
            {
                LoadTrashAsync();
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposedResources)
        {
            _disposedResources = true;
            _lifetime.Cancel();
            _recordThumbnails.Dispose();
            _trashThumbnails.Dispose();
            _lifetime.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control CreateLayout()
    {
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _usageLabel.AutoEllipsis = true;
        _usageLabel.Dock = DockStyle.Fill;
        _usageLabel.Font = new Font(Font, FontStyle.Bold);
        _usageLabel.Text = "저장 공간 계산 중…";

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(new TabPage("기록") { Controls = { CreateRecordsTab() } });
        _tabs.TabPages.Add(new TabPage("휴지통") { Controls = { CreateTrashTab() } });
        _tabs.TabPages.Add(new TabPage("설정") { Controls = { CreateSettingsTab() } });

        root.Controls.Add(_usageLabel, 0, 0);
        root.Controls.Add(_tabs, 0, 1);
        return root;
    }

    private Control CreateRecordsTab()
    {
        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 280,
        };

        _dateTree.Dock = DockStyle.Fill;
        _dateTree.BeforeExpand += DateTreeOnBeforeExpand;
        _dateTree.AfterSelect += DateTreeOnAfterSelect;
        _dateTree.AfterCollapse += DateTreeOnAfterCollapse;
        split.Panel1.Controls.Add(_dateTree);

        TableLayoutPanel right = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        ConfigureRecordList(_recordList);
        _recordList.SmallImageList = _recordThumbnails;
        _recordList.DoubleClick += (_, _) => RestoreSelectedAsync(_recordList);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };
        Button deleteRecord = new() { AutoSize = true, Text = "선택 기록 삭제" };
        deleteRecord.Click += (_, _) => DeleteSelectedRecordAsync();
        Button deleteGroup = new() { AutoSize = true, Text = "선택 기간 삭제" };
        deleteGroup.Click += (_, _) => DeleteSelectedGroupAsync();
        _loadMoreButton.AutoSize = true;
        _loadMoreButton.Text = "추가 로드";
        _loadMoreButton.Visible = false;
        _loadMoreButton.Click += (_, _) => LoadSelectedDayAsync(append: true);
        actions.Controls.Add(deleteRecord);
        actions.Controls.Add(deleteGroup);
        actions.Controls.Add(_loadMoreButton);

        right.Controls.Add(_recordList, 0, 0);
        right.Controls.Add(actions, 0, 1);
        split.Panel2.Controls.Add(right);
        return split;
    }

    private Control CreateTrashTab()
    {
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        ConfigureRecordList(_trashList);
        _trashList.SmallImageList = _trashThumbnails;
        _trashList.CheckBoxes = true;
        _trashList.Columns.Add("삭제 시각", 145);
        _trashList.Columns.Add("영구 삭제 예정", 145);
        _trashList.Columns.Add("남은 기간", 90);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };
        Button empty = new() { AutoSize = true, Text = "휴지통 전체 비우기" };
        empty.Click += (_, _) => EmptyTrashAsync();
        Button restoreChecked = new() { AutoSize = true, Text = "선택 복원" };
        restoreChecked.Click += (_, _) => RestoreTrashAsync(checkedOnly: true);
        Button restoreOne = new() { AutoSize = true, Text = "개별 복원" };
        restoreOne.Click += (_, _) => RestoreTrashAsync(checkedOnly: false);
        Button refresh = new() { AutoSize = true, Text = "새로 고침" };
        refresh.Click += (_, _) => LoadTrashAsync();
        actions.Controls.Add(empty);
        actions.Controls.Add(restoreChecked);
        actions.Controls.Add(restoreOne);
        actions.Controls.Add(refresh);
        layout.Controls.Add(_trashList, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        return layout;
    }

    private Control CreateSettingsTab()
    {
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(24),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _automaticDeletion.AutoSize = true;
        _automaticDeletion.Text = "자동 삭제 사용";
        _automaticDeletion.Click += (_, _) => SaveSettingsFromControlsAsync();
        _retentionPeriod.DropDownStyle = ComboBoxStyle.DropDownList;
        _retentionPeriod.Items.AddRange([
            new RetentionOption("1개월", RetentionPeriod.OneMonth),
            new RetentionOption("3개월", RetentionPeriod.ThreeMonths),
            new RetentionOption("1년", RetentionPeriod.OneYear),
        ]);
        _retentionPeriod.SelectedIndexChanged += (_, _) =>
        {
            if (!_loadingSettings && _automaticDeletion.Checked)
            {
                SaveSettingsFromControlsAsync();
            }
        };
        _settingsStatus.AutoSize = true;
        _settingsStatus.ForeColor = SystemColors.GrayText;

        layout.Controls.Add(new Label { AutoSize = true, Text = "자동 삭제 사용:" }, 0, 0);
        layout.Controls.Add(_automaticDeletion, 1, 0);
        layout.Controls.Add(new Label { AutoSize = true, Text = "보관 기간:" }, 0, 1);
        layout.Controls.Add(_retentionPeriod, 1, 1);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "삭제 대상은 먼저 휴지통으로 이동하며 7일 동안 복구할 수 있습니다.",
        }, 1, 2);
        layout.Controls.Add(_settingsStatus, 1, 3);
        return layout;
    }

    private static void ConfigureRecordList(ListView list)
    {
        list.Dock = DockStyle.Fill;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = true;
        list.View = View.Details;
        list.Columns.Add("종류", 70);
        list.Columns.Add("복사 시각", 145);
        list.Columns.Add("내용 / OCR", 390);
        list.Columns.Add("이미지", 110);
    }

    private async void InitializeAsync()
    {
        await Task.WhenAll(LoadYearsAsync(), RefreshUsageAsync(), LoadSettingsAsync());
    }

    private async Task LoadYearsAsync()
    {
        long generation = ++_treeGeneration;
        try
        {
            IReadOnlyList<HistoryGroupSummary> years = await _repository
                .GetYearGroupsAsync(_timeZone, _lifetime.Token);
            if (!CanApply(generation, _treeGeneration))
            {
                return;
            }

            _dateTree.Nodes.Clear();
            foreach (HistoryGroupSummary year in years)
            {
                HistoryDateRange range = new(
                    HistoryGroupLevel.Year,
                    year.StartUtc,
                    year.EndUtc,
                    $"{year.Value}년");
                _dateTree.Nodes.Add(CreateGroupNode(year, range, year.Value, null));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("history_years", exception);
        }
    }

    private async void DateTreeOnBeforeExpand(object? sender, TreeViewCancelEventArgs eventArgs)
    {
        TreeNode? node = eventArgs.Node;
        if (node?.Tag is not HistoryNodeTag tag || tag.ChildrenLoaded)
        {
            return;
        }

        tag.ChildrenLoaded = true;
        node.Nodes.Clear();
        try
        {
            if (tag.Range.Level == HistoryGroupLevel.Day)
            {
                IReadOnlyList<ClipboardHistoryEntry> records = await _repository.GetActiveRecordsAsync(
                    tag.Range,
                    0,
                    ClipboardHistoryRepository.HistoryPageSize,
                    _lifetime.Token);
                foreach (ClipboardHistoryEntry record in records)
                {
                    node.Nodes.Add(new TreeNode(
                        $"{record.CopiedAt.ToLocalTime():HH:mm:ss}  " +
                        $"{(record.RecordType == ClipboardRecordType.Image ? "[이미지] " : string.Empty)}" +
                        record.Preview)
                    {
                        Tag = record,
                    });
                }

                if (records.Count == ClipboardHistoryRepository.HistoryPageSize)
                {
                    node.Nodes.Add(new TreeNode("추가 기록은 오른쪽 목록에서 불러올 수 있습니다."));
                }

                return;
            }

            IReadOnlyList<HistoryGroupSummary> children = tag.Range.Level == HistoryGroupLevel.Year
                ? await _repository.GetMonthGroupsAsync(tag.Year, _timeZone, _lifetime.Token)
                : await _repository.GetDayGroupsAsync(
                    tag.Year,
                    tag.Month!.Value,
                    _timeZone,
                    _lifetime.Token);
            foreach (HistoryGroupSummary child in children)
            {
                HistoryDateRange range = tag.Range.Level == HistoryGroupLevel.Year
                    ? HistoryDateRangeCalculator.ForMonth(tag.Year, child.Value, _timeZone)
                    : HistoryDateRangeCalculator.ForDay(
                        tag.Year,
                        tag.Month!.Value,
                        child.Value,
                        _timeZone);
                node.Nodes.Add(CreateGroupNode(
                    child,
                    range,
                    tag.Year,
                    tag.Range.Level == HistoryGroupLevel.Year ? child.Value : tag.Month));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("history_groups", exception);
            tag.ChildrenLoaded = false;
        }
    }

    private void DateTreeOnAfterCollapse(object? sender, TreeViewEventArgs eventArgs)
    {
        if (eventArgs.Node?.Tag is HistoryNodeTag { Range.Level: HistoryGroupLevel.Day } tag)
        {
            eventArgs.Node.Nodes.Clear();
            eventArgs.Node.Nodes.Add(new TreeNode("불러오는 중…"));
            tag.ChildrenLoaded = false;
        }
    }

    private void DateTreeOnAfterSelect(object? sender, TreeViewEventArgs eventArgs)
    {
        if (eventArgs.Node?.Tag is HistoryNodeTag { Range.Level: HistoryGroupLevel.Day } tag)
        {
            _selectedRange = tag.Range;
            _recordOffset = 0;
            LoadSelectedDayAsync(append: false);
        }
    }

    private async void LoadSelectedDayAsync(bool append)
    {
        if (_selectedRange is not HistoryDateRange range)
        {
            return;
        }

        long generation = ++_recordGeneration;
        int offset = append ? _recordOffset : 0;
        try
        {
            IReadOnlyList<ClipboardHistoryEntry> records = await _repository.GetActiveRecordsAsync(
                range,
                offset,
                ClipboardHistoryRepository.HistoryPageSize,
                _lifetime.Token);
            if (!CanApply(generation, _recordGeneration))
            {
                return;
            }

            if (!append)
            {
                _recordList.Items.Clear();
                _recordThumbnails.Images.Clear();
                _thumbnailLoads.Clear();
                _recordOffset = 0;
            }

            foreach (ClipboardHistoryEntry record in records)
            {
                _recordList.Items.Add(CreateRecordItem(record, includeTrashColumns: false));
            }

            _recordOffset += records.Count;
            _loadMoreButton.Visible = records.Count == ClipboardHistoryRepository.HistoryPageSize;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("history_records", exception);
        }
    }

    private async void DeleteSelectedRecordAsync()
    {
        if (_recordList.SelectedItems.Count == 0)
        {
            return;
        }

        List<ClipboardHistoryEntry> records = _recordList.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => (ClipboardHistoryEntry)item.Tag!)
            .ToList();
        DeletionSummary summary = new(
            "선택한 기록",
            records.Count,
            records.Count(record => record.RecordType == ClipboardRecordType.Image),
            records.Where(record => record.RecordType == ClipboardRecordType.Image)
                .Sum(record => record.ImageBytes));
        if (!ConfirmSoftDelete(summary))
        {
            return;
        }

        await _writer.SoftDeleteIdsAsync(
            records.Select(record => record.Id).ToArray(),
            DateTimeOffset.UtcNow,
            _lifetime.Token);
        await RefreshAfterMutationAsync();
    }

    private async void DeleteSelectedGroupAsync()
    {
        if (_dateTree.SelectedNode?.Tag is not HistoryNodeTag tag)
        {
            return;
        }

        DeletionSummary summary = await _repository.GetDeletionSummaryAsync(
            tag.Range,
            _lifetime.Token);
        if (summary.RecordCount == 0 || !ConfirmSoftDelete(summary))
        {
            return;
        }

        await _writer.SoftDeleteRangeAsync(
            tag.Range.StartUtc,
            tag.Range.EndUtc,
            DateTimeOffset.UtcNow,
            _lifetime.Token);
        await RefreshAfterMutationAsync();
    }

    private bool ConfirmSoftDelete(DeletionSummary summary) => MessageBox.Show(
        this,
        $"삭제 대상 기간: {summary.PeriodDisplay}\n" +
        $"기록: {summary.RecordCount:N0}개\n" +
        $"이미지: {summary.ImageCount:N0}개\n" +
        $"예상 이미지 용량: {ByteSizeFormatter.Format(summary.ImageBytes)}\n\n" +
        "7일 동안 휴지통에서 복구할 수 있습니다.",
        "기록 삭제 확인",
        MessageBoxButtons.OKCancel,
        MessageBoxIcon.Warning) == DialogResult.OK;

    private async void LoadTrashAsync()
    {
        long generation = ++_trashGeneration;
        try
        {
            IReadOnlyList<ClipboardHistoryEntry> records = await _repository
                .GetTrashAsync(0, 500, _lifetime.Token);
            if (!CanApply(generation, _trashGeneration))
            {
                return;
            }

            _trashList.Items.Clear();
            _trashThumbnails.Images.Clear();
            _thumbnailLoads.Clear();
            foreach (ClipboardHistoryEntry record in records)
            {
                _trashList.Items.Add(CreateRecordItem(record, includeTrashColumns: true));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("trash_records", exception);
        }
    }

    private async void RestoreTrashAsync(bool checkedOnly)
    {
        IEnumerable<ListViewItem> selected = checkedOnly
            ? _trashList.CheckedItems.Cast<ListViewItem>()
            : _trashList.SelectedItems.Cast<ListViewItem>().Take(1);
        long[] ids = selected.Select(item => ((ClipboardHistoryEntry)item.Tag!).Id).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await _writer.RestoreIdsAsync(ids, _lifetime.Token);
        await RefreshAfterMutationAsync();
        LoadTrashAsync();
    }

    private async void EmptyTrashAsync()
    {
        DeletionSummary summary = await _repository.GetTrashSummaryAsync(_lifetime.Token);
        if (summary.RecordCount == 0)
        {
            return;
        }

        DialogResult result = MessageBox.Show(
            this,
            $"휴지통의 {summary.RecordCount:N0}개 기록과 참조되지 않는 이미지 " +
            $"{ByteSizeFormatter.Format(summary.ImageBytes)}를 영구 삭제합니다.\n" +
            "이 작업은 복구할 수 없습니다.",
            "휴지통 비우기",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (result != DialogResult.OK)
        {
            return;
        }

        await _retentionService.EmptyTrashAsync(_lifetime.Token);
        LoadTrashAsync();
        await RefreshAfterMutationAsync();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            AppSettings settings = await _settingsRepository.LoadAsync(_lifetime.Token);
            if (IsDisposed)
            {
                return;
            }

            _loadingSettings = true;
            _automaticDeletion.Checked = settings.AutomaticDeletionEnabled;
            _retentionPeriod.SelectedItem = _retentionPeriod.Items
                .Cast<RetentionOption>()
                .First(option => option.Value == settings.RetentionPeriod);
            _settingsStatus.Text = settings.AutomaticDeletionEnabled
                ? "자동 삭제가 켜져 있습니다."
                : "안전 기본값: 자동 삭제 꺼짐";
            _loadingSettings = false;
        }
        catch (Exception exception)
        {
            _loadingSettings = false;
            _logger.LogUnhandledException("settings_load", exception);
            _settingsStatus.Text = "설정을 불러오지 못했습니다.";
        }
    }

    private async void SaveSettingsFromControlsAsync()
    {
        if (_loadingSettings || _retentionPeriod.SelectedItem is not RetentionOption option)
        {
            return;
        }

        AppSettings previous = await _settingsRepository.LoadAsync(_lifetime.Token);
        AppSettings requested = new(_automaticDeletion.Checked, option.Value);
        if (requested.AutomaticDeletionEnabled)
        {
            DateTimeOffset cutoff = HistoryDateRangeCalculator.CalculateRetentionCutoffUtc(
                DateTimeOffset.UtcNow,
                _timeZone,
                requested.RetentionPeriod);
            DeletionSummary summary = await _repository.GetOlderThanSummaryAsync(
                cutoff,
                option.DisplayName,
                _lifetime.Token);
            DialogResult confirmation = MessageBox.Show(
                this,
                $"보관 기간: {option.DisplayName}\n" +
                $"휴지통으로 이동할 기록: {summary.RecordCount:N0}개\n" +
                $"이미지: {summary.ImageCount:N0}개\n" +
                $"예상 용량: {ByteSizeFormatter.Format(summary.ImageBytes)}\n\n" +
                "대상 기록은 바로 영구 삭제되지 않고 7일간 휴지통에 보관됩니다.",
                "자동 삭제 설정 확인",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (confirmation != DialogResult.OK)
            {
                _loadingSettings = true;
                _automaticDeletion.Checked = previous.AutomaticDeletionEnabled;
                _retentionPeriod.SelectedItem = _retentionPeriod.Items
                    .Cast<RetentionOption>()
                    .First(item => item.Value == previous.RetentionPeriod);
                _loadingSettings = false;
                return;
            }
        }

        await _settingsRepository.SaveAsync(requested, _lifetime.Token);
        _settingsStatus.Text = requested.AutomaticDeletionEnabled
            ? $"자동 삭제 사용 · {option.DisplayName} 보관"
            : "자동 삭제 꺼짐";
        if (requested.AutomaticDeletionEnabled)
        {
            await _retentionService.RunNowAsync(_lifetime.Token);
            await RefreshAfterMutationAsync();
        }
    }

    private async void RestoreSelectedAsync(ListView list)
    {
        if (list.SelectedItems.Count == 0 ||
            list.SelectedItems[0].Tag is not ClipboardHistoryEntry entry)
        {
            return;
        }

        try
        {
            await _restoreService.RestoreAsync(entry, _lifetime.Token);
        }
        catch (Exception exception)
        {
            _logger.LogClipboardRestoreFailure(exception);
            MessageBox.Show(
                this,
                "기록을 클립보드에 복원하지 못했습니다. 이미지 파일이 누락되었거나 손상되었을 수 있습니다.",
                "복원 실패",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task RefreshAfterMutationAsync()
    {
        _storageUsage.Invalidate();
        _selectedRange = null;
        _recordList.Items.Clear();
        await Task.WhenAll(LoadYearsAsync(), RefreshUsageAsync());
    }

    private async Task RefreshUsageAsync()
    {
        long generation = ++_usageGeneration;
        try
        {
            StorageUsage usage = await _storageUsage.CalculateAsync(
                force: true,
                _lifetime.Token);
            if (!CanApply(generation, _usageGeneration))
            {
                return;
            }

            _usageLabel.Text =
                $"전체 사용 공간: {ByteSizeFormatter.Format(usage.TotalBytes)}    " +
                $"활성 이미지: {ByteSizeFormatter.Format(usage.ActiveImageBytes)}    " +
                $"휴지통: {ByteSizeFormatter.Format(usage.TrashImageBytes)}    " +
                $"DB/WAL: {ByteSizeFormatter.Format(usage.DatabaseBytes)}    " +
                $"전체 기록: {usage.TotalRecordCount:N0}개";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogUnhandledException("storage_usage", exception);
            if (!IsDisposed)
            {
                _usageLabel.Text = "저장 공간을 계산하지 못했습니다.";
            }
        }
    }

    private TreeNode CreateGroupNode(
        HistoryGroupSummary summary,
        HistoryDateRange range,
        int year,
        int? month)
    {
        TreeNode node = new(
            $"{summary.DisplayName}  ({summary.RecordCount:N0}개 · {ByteSizeFormatter.Format(summary.ImageBytes)})")
        {
            Tag = new HistoryNodeTag(range, year, month),
        };
        node.Nodes.Add(new TreeNode("불러오는 중…"));

        return node;
    }

    private ListViewItem CreateRecordItem(ClipboardHistoryEntry record, bool includeTrashColumns)
    {
        string imageDescription = record.RecordType == ClipboardRecordType.Image
            ? $"{record.ImageWidth}×{record.ImageHeight} · {ByteSizeFormatter.Format(record.ImageBytes)}" +
              (IsImageMissing(record) ? " · 파일 누락" : string.Empty)
            : string.Empty;
        ListViewItem item = new(record.RecordType == ClipboardRecordType.Image ? "이미지" : "텍스트")
        {
            Tag = record,
        };
        item.SubItems.Add(record.CopiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        item.SubItems.Add(record.Preview);
        item.SubItems.Add(imageDescription);
        if (includeTrashColumns)
        {
            DateTimeOffset deleted = record.DeletedAtUtc ?? DateTimeOffset.MinValue;
            DateTimeOffset purge = deleted + HistoryRetentionService.TrashRetention;
            double remaining = Math.Max(0, (purge - DateTimeOffset.UtcNow).TotalDays);
            item.SubItems.Add(deleted.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(purge.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add($"{Math.Ceiling(remaining):0}일");
        }

        if (record.RecordType == ClipboardRecordType.Image &&
            !string.IsNullOrWhiteSpace(record.ImageRelativePath))
        {
            QueueThumbnail(record, item, includeTrashColumns);
        }

        return item;
    }

    private async void QueueThumbnail(
        ClipboardHistoryEntry record,
        ListViewItem item,
        bool trash)
    {
        ImageList images = trash ? _trashThumbnails : _recordThumbnails;
        if (images.Images.Count >= 64 || !_thumbnailLoads.Add(record.Id))
        {
            return;
        }

        try
        {
            using Bitmap source = await Task.Run(() =>
                _imageStore.LoadBitmapAsync(record.ImageRelativePath!, _lifetime.Token));
            using Bitmap thumbnail = new(source, images.ImageSize);
            ListView list = trash ? _trashList : _recordList;
            if (IsDisposed || _lifetime.IsCancellationRequested || !list.Items.Contains(item))
            {
                return;
            }

            string key = record.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!images.Images.ContainsKey(key) && images.Images.Count < 64)
            {
                images.Images.Add(key, thumbnail);
                item.ImageKey = key;
            }
        }
        catch
        {
            // Missing and corrupt files remain visible as records without a thumbnail.
        }
        finally
        {
            _thumbnailLoads.Remove(record.Id);
        }
    }

    private bool IsImageMissing(ClipboardHistoryEntry record)
    {
        if (record.RecordType != ClipboardRecordType.Image ||
            string.IsNullOrWhiteSpace(record.ImageRelativePath))
        {
            return false;
        }

        try
        {
            return !File.Exists(_imageStore.ResolveManagedPath(record.ImageRelativePath));
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private bool CanApply(long generation, long currentGeneration) =>
        !IsDisposed && !_lifetime.IsCancellationRequested && generation == currentGeneration;

    private static ImageList CreateThumbnailList() => new()
    {
        ColorDepth = ColorDepth.Depth32Bit,
        ImageSize = new Size(48, 48),
        TransparentColor = Color.Transparent,
    };

    private sealed class HistoryNodeTag(
        HistoryDateRange range,
        int year,
        int? month)
    {
        public HistoryDateRange Range { get; } = range;

        public int Year { get; } = year;

        public int? Month { get; } = month;

        public bool ChildrenLoaded { get; set; }
    }

    private sealed record RetentionOption(string DisplayName, RetentionPeriod Value)
    {
        public override string ToString() => DisplayName;
    }
}
