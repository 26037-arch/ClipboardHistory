using System.Drawing;

namespace ClipboardHistory;

internal sealed class ClipboardHistoryPopup : Form, IClipboardHistoryPopup
{
    private static readonly Size DefaultPopupSize = new(420, 500);

    private readonly ClipboardHistoryRepository _repository;
    private readonly ClipboardRestoreService _restoreService;
    private readonly ClipboardImageStore _imageStore;
    private readonly Action _openHistoryManager;
    private readonly AppLogger _logger;
    private readonly TextBox _searchBox;
    private readonly ListBox _resultsList;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly Font _timestampFont;
    private readonly Font _titleFont;
    private readonly Dictionary<long, Bitmap> _thumbnails = [];
    private readonly HashSet<long> _thumbnailLoads = [];
    private readonly Queue<long> _thumbnailOrder = [];
    private const int MaximumThumbnails = 24;

    private CancellationTokenSource? _searchCancellation;
    private long _searchGeneration;
    private long _scheduledGeneration;
    private bool _restoreInProgress;
    private bool _closing;
    private bool _popupResourcesDisposed;

    public ClipboardHistoryPopup(
        ClipboardHistoryRepository repository,
        ClipboardRestoreService restoreService,
        ClipboardImageStore imageStore,
        Action openHistoryManager,
        AppLogger logger)
    {
        _repository = repository;
        _restoreService = restoreService;
        _imageStore = imageStore;
        _openHistoryManager = openHistoryManager;
        _logger = logger;

        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SystemColors.Window;
        ClientSize = DefaultPopupSize;
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        SizeGripStyle = SizeGripStyle.Hide;
        StartPosition = FormStartPosition.Manual;
        Text = "클립보드 기록";
        TopMost = true;

        FontFamily fontFamily = SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;
        _timestampFont = new Font(fontFamily, 8.25f, FontStyle.Regular);
        _titleFont = new Font(fontFamily, 12f, FontStyle.Bold);
        _searchBox = CreateSearchBox();
        _resultsList = CreateResultsList();
        _statusLabel = CreateStatusLabel();
        _debounceTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _debounceTimer.Tick += DebounceTimerOnTick;

        Controls.Add(CreateLayout());

        _searchBox.TextChanged += SearchBoxOnTextChanged;
        _searchBox.KeyDown += SearchBoxOnKeyDown;
        _resultsList.KeyDown += ResultsListOnKeyDown;
        _resultsList.MouseClick += ResultsListOnMouseClick;
        _resultsList.DrawItem += ResultsListOnDrawItem;
        Deactivate += PopupOnDeactivate;
        DpiChanged += PopupOnDpiChanged;

        PositionNearCursor(DefaultPopupSize);
    }

    public event EventHandler? PopupClosed;

    public void ShowPopup() => Show();

    public void ClosePopup()
    {
        if (!IsDisposed)
        {
            Close();
        }
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        PositionNearCursor(Size);
        Activate();
        _searchBox.Focus();
        QueueSearch(immediate: true);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Escape)
        {
            eventArgs.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(eventArgs);
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        _closing = true;
        TopMost = false;
        _searchGeneration++;
        _debounceTimer.Stop();
        _searchCancellation?.Cancel();
        base.OnFormClosing(eventArgs);
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        base.OnFormClosed(eventArgs);
        PopupClosed?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_popupResourcesDisposed)
        {
            _popupResourcesDisposed = true;
            _closing = true;
            _debounceTimer.Stop();
            _debounceTimer.Tick -= DebounceTimerOnTick;
            _debounceTimer.Dispose();
            _searchCancellation?.Cancel();
            DisposeThumbnails();
            _timestampFont.Dispose();
            _titleFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private Control CreateLayout()
    {
        TableLayoutPanel layout = new()
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 4,
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        Label title = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = _titleFont,
            Text = "클립보드 기록",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(_searchBox, 0, 1);
        layout.Controls.Add(_resultsList, 0, 2);
        TableLayoutPanel footer = new()
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Button allRecordsButton = new()
        {
            AutoSize = true,
            Text = "모든 기록 보기",
            Margin = new Padding(6, 2, 0, 0),
        };
        allRecordsButton.Click += (_, _) =>
        {
            _openHistoryManager();
            Close();
        };
        footer.Controls.Add(_statusLabel, 0, 0);
        footer.Controls.Add(allRecordsButton, 1, 0);
        layout.Controls.Add(footer, 0, 3);
        return layout;
    }

    private static TextBox CreateSearchBox() => new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        PlaceholderText = "클립보드 기록 검색",
    };

    private static ListBox CreateResultsList() => new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        DrawMode = DrawMode.OwnerDrawFixed,
        IntegralHeight = false,
        ItemHeight = 72,
    };

    private static Label CreateStatusLabel() => new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        ForeColor = SystemColors.GrayText,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private void PositionNearCursor(Size desiredSize)
    {
        Point cursor = Cursor.Position;
        Rectangle workingArea = Screen.FromPoint(cursor).WorkingArea;
        Bounds = PopupBoundsCalculator.Calculate(cursor, workingArea, desiredSize);
    }

    private void QueueSearch(bool immediate = false)
    {
        if (_closing)
        {
            return;
        }

        _searchGeneration++;
        _scheduledGeneration = _searchGeneration;
        _searchCancellation?.Cancel();
        _debounceTimer.Stop();

        if (immediate)
        {
            StartSearchAsync(_scheduledGeneration, _searchBox.Text);
        }
        else
        {
            _debounceTimer.Start();
        }
    }

    private async void StartSearchAsync(long generation, string query)
    {
        if (_closing || generation != _searchGeneration)
        {
            return;
        }

        CancellationTokenSource cancellation = new();
        _searchCancellation = cancellation;
        SetStatus("검색 중…", isError: false);

        try
        {
            IReadOnlyList<ClipboardHistoryEntry> results = await Task.Run(
                () => _repository.SearchAsync(
                    query,
                    ClipboardHistoryRepository.MaximumResults,
                    cancellation.Token),
                cancellation.Token);

            if (_closing || cancellation.IsCancellationRequested || generation != _searchGeneration)
            {
                return;
            }

            ReplaceResults(results);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer search or popup close superseded this operation.
        }
        catch (Exception exception)
        {
            if (!_closing && generation == _searchGeneration)
            {
                _logger.LogClipboardSearchFailure(exception);
                SetStatus("검색 중 오류가 발생했습니다.", isError: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void ReplaceResults(IReadOnlyList<ClipboardHistoryEntry> results)
    {
        DisposeThumbnails();
        _resultsList.BeginUpdate();
        try
        {
            _resultsList.Items.Clear();
            foreach (ClipboardHistoryEntry result in results)
            {
                _resultsList.Items.Add(result);
            }

            if (_resultsList.Items.Count > 0)
            {
                _resultsList.SelectedIndex = 0;
            }
        }
        finally
        {
            _resultsList.EndUpdate();
        }

        SetStatus(
            results.Count == 0 ? "검색 결과가 없습니다." : $"{results.Count}개 기록",
            isError: false);
    }

    private async void RestoreSelectedAsync()
    {
        if (_restoreInProgress || _resultsList.SelectedItem is not ClipboardHistoryEntry entry)
        {
            return;
        }

        _restoreInProgress = true;
        _searchBox.Enabled = false;
        _resultsList.Enabled = false;
        SetStatus("클립보드에 복사하는 중…", isError: false);

        try
        {
            await _restoreService.RestoreAsync(entry);
            if (!_closing)
            {
                Close();
            }
        }
        catch (Exception exception)
        {
            if (!_closing)
            {
                _logger.LogClipboardRestoreFailure(exception);
                SetStatus(
                    entry.RecordType == ClipboardRecordType.Image
                        ? "원본 이미지를 불러오거나 복사하지 못했습니다."
                        : "클립보드에 복사하지 못했습니다.",
                    isError: true);
            }
        }
        finally
        {
            _restoreInProgress = false;
            if (!_closing)
            {
                _searchBox.Enabled = true;
                _resultsList.Enabled = true;
            }
        }
    }

    private void SetStatus(string text, bool isError)
    {
        _statusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.GrayText;
        _statusLabel.Text = text;
    }

    private void SearchBoxOnTextChanged(object? sender, EventArgs eventArgs) => QueueSearch();

    private void SearchBoxOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Down && _resultsList.Items.Count > 0)
        {
            _resultsList.SelectedIndex = Math.Max(0, _resultsList.SelectedIndex);
            _resultsList.Focus();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
        }
        else if (eventArgs.KeyCode == Keys.Enter && _resultsList.Items.Count > 0)
        {
            _resultsList.SelectedIndex = Math.Max(0, _resultsList.SelectedIndex);
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            RestoreSelectedAsync();
        }
        else if (eventArgs.KeyCode == Keys.Escape)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            Close();
        }
    }

    private void ResultsListOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Enter)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            RestoreSelectedAsync();
        }
        else if (eventArgs.KeyCode == Keys.Escape)
        {
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            Close();
        }
    }

    private void ResultsListOnMouseClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left)
        {
            return;
        }

        int index = _resultsList.IndexFromPoint(eventArgs.Location);
        if (index >= 0)
        {
            _resultsList.SelectedIndex = index;
            RestoreSelectedAsync();
        }
    }

    private void ResultsListOnDrawItem(object? sender, DrawItemEventArgs eventArgs)
    {
        if (eventArgs.Index < 0 || eventArgs.Index >= _resultsList.Items.Count)
        {
            return;
        }

        eventArgs.DrawBackground();
        ClipboardHistoryEntry entry = (ClipboardHistoryEntry)_resultsList.Items[eventArgs.Index];
        Color textColor = (eventArgs.State & DrawItemState.Selected) != 0
            ? SystemColors.HighlightText
            : SystemColors.ControlText;

        int contentLeft = eventArgs.Bounds.X + 8;
        if (entry.RecordType == ClipboardRecordType.Image)
        {
            Rectangle thumbnailBounds = new(eventArgs.Bounds.X + 6, eventArgs.Bounds.Y + 6, 58, 58);
            if (_thumbnails.TryGetValue(entry.Id, out Bitmap? thumbnail))
            {
                eventArgs.Graphics.DrawImage(thumbnail, thumbnailBounds);
            }
            else
            {
                using Pen border = new(SystemColors.ControlDark);
                eventArgs.Graphics.DrawRectangle(border, thumbnailBounds);
                QueueThumbnail(entry);
            }

            contentLeft += 66;
        }

        Rectangle timestampBounds = new(
            contentLeft,
            eventArgs.Bounds.Y + 4,
            eventArgs.Bounds.Right - contentLeft - 8,
            18);

        Rectangle previewBounds = new(
            contentLeft,
            eventArgs.Bounds.Y + 23,
            eventArgs.Bounds.Right - contentLeft - 8,
            eventArgs.Bounds.Height - 25);

        TextRenderer.DrawText(
            eventArgs.Graphics,
            entry.RecordType == ClipboardRecordType.Image
                ? $"{entry.CopiedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {entry.ImageWidth}×{entry.ImageHeight}"
                : entry.CopiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            _timestampFont,
            timestampBounds,
            textColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(
            eventArgs.Graphics,
            entry.Preview,
            Font,
            previewBounds,
            textColor,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        eventArgs.DrawFocusRectangle();
    }

    private void DebounceTimerOnTick(object? sender, EventArgs eventArgs)
    {
        _debounceTimer.Stop();
        StartSearchAsync(_scheduledGeneration, _searchBox.Text);
    }

    private void PopupOnDeactivate(object? sender, EventArgs eventArgs)
    {
        if (!_closing)
        {
            Close();
        }
    }

    private void PopupOnDpiChanged(object? sender, DpiChangedEventArgs eventArgs)
    {
        if (!_closing && IsHandleCreated)
        {
            BeginInvoke(() => PositionNearCursor(Size));
        }
    }

    private async void QueueThumbnail(ClipboardHistoryEntry entry)
    {
        if (_closing || string.IsNullOrWhiteSpace(entry.ImageRelativePath) ||
            !_thumbnailLoads.Add(entry.Id))
        {
            return;
        }

        long generation = _searchGeneration;

        try
        {
            using Bitmap source = await Task.Run(() => _imageStore.LoadBitmapAsync(entry.ImageRelativePath));
            Bitmap thumbnail = new(source, new Size(58, 58));
            if (_closing || generation != _searchGeneration || !_resultsList.Items.Contains(entry))
            {
                thumbnail.Dispose();
                return;
            }

            _thumbnails[entry.Id] = thumbnail;
            _thumbnailOrder.Enqueue(entry.Id);
            while (_thumbnailOrder.Count > MaximumThumbnails)
            {
                long expired = _thumbnailOrder.Dequeue();
                if (_thumbnails.Remove(expired, out Bitmap? old))
                {
                    old.Dispose();
                }
            }

            _resultsList.Invalidate();
        }
        catch
        {
            // A missing or corrupt thumbnail does not make the search result unusable.
        }
        finally
        {
            _thumbnailLoads.Remove(entry.Id);
        }
    }

    private void DisposeThumbnails()
    {
        foreach (Bitmap thumbnail in _thumbnails.Values)
        {
            thumbnail.Dispose();
        }

        _thumbnails.Clear();
        _thumbnailLoads.Clear();
        _thumbnailOrder.Clear();
    }
}
