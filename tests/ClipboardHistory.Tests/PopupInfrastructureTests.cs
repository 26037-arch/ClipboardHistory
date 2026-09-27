using System.Drawing;

namespace ClipboardHistory.Tests;

public sealed class PopupInfrastructureTests
{
    [Theory]
    [InlineData(100, 100)]
    [InlineData(1910, 1030)]
    [InlineData(0, 0)]
    public void PopupBounds_StayInsideSingleMonitorWorkingArea(int cursorX, int cursorY)
    {
        Rectangle workingArea = new(0, 0, 1920, 1040);

        Rectangle bounds = PopupBoundsCalculator.Calculate(
            new Point(cursorX, cursorY),
            workingArea,
            new Size(420, 500));

        Assert.True(workingArea.Contains(bounds));
    }

    [Fact]
    public void PopupBounds_StayInsideNegativeCoordinateSecondaryMonitor()
    {
        Rectangle secondaryWorkingArea = new(-1920, -120, 1920, 1080);

        Rectangle bounds = PopupBoundsCalculator.Calculate(
            new Point(-10, 940),
            secondaryWorkingArea,
            new Size(420, 500));

        Assert.True(secondaryWorkingArea.Contains(bounds));
    }

    [Fact]
    public void PopupBounds_ShrinkWhenWorkingAreaIsSmallerThanPopup()
    {
        Rectangle workingArea = new(100, 200, 300, 240);

        Rectangle bounds = PopupBoundsCalculator.Calculate(
            new Point(250, 320),
            workingArea,
            new Size(420, 500));

        Assert.Equal(workingArea, bounds);
    }

    [Fact]
    public void PopupController_RepeatedToggleDoesNotAccumulateHandlersOrResources()
    {
        List<FakePopup> popups = [];
        using ClipboardPopupController controller = new(() =>
        {
            FakePopup popup = new();
            popups.Add(popup);
            return popup;
        });

        for (int index = 0; index < 50; index++)
        {
            controller.Toggle();
            Assert.True(controller.HasOpenPopup);
            controller.Toggle();
            Assert.False(controller.HasOpenPopup);
        }

        Assert.Equal(50, popups.Count);
        Assert.All(popups, popup =>
        {
            Assert.Equal(1, popup.ShowCount);
            Assert.Equal(1, popup.CloseCount);
            Assert.Equal(1, popup.DisposeCount);
            Assert.Equal(0, popup.SubscriberCount);
        });
    }

    [Fact]
    public void PopupController_RepeatedExternalCloseReleasesEachPopup()
    {
        List<FakePopup> popups = [];
        using ClipboardPopupController controller = new(() =>
        {
            FakePopup popup = new();
            popups.Add(popup);
            return popup;
        });

        for (int index = 0; index < 50; index++)
        {
            controller.Toggle();
            popups[^1].RaiseClosed();
            Assert.False(controller.HasOpenPopup);
        }

        Assert.All(popups, popup =>
        {
            Assert.Equal(1, popup.DisposeCount);
            Assert.Equal(0, popup.SubscriberCount);
        });
    }

    private sealed class FakePopup : IClipboardHistoryPopup
    {
        private EventHandler? _popupClosed;

        public int ShowCount { get; private set; }

        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int SubscriberCount { get; private set; }

        public event EventHandler? PopupClosed
        {
            add
            {
                _popupClosed += value;
                SubscriberCount++;
            }
            remove
            {
                _popupClosed -= value;
                SubscriberCount--;
            }
        }

        public void ShowPopup() => ShowCount++;

        public void ClosePopup()
        {
            CloseCount++;
            _popupClosed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => DisposeCount++;

        public void RaiseClosed() => _popupClosed?.Invoke(this, EventArgs.Empty);
    }
}
