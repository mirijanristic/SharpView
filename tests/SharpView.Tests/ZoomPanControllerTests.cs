using SharpView.Rendering;
using Xunit;

namespace SharpView.Tests;

public sealed class ZoomPanControllerTests
{
    [Fact]
    public void Fit_UsesTheSmallerScaleAxis_AndResetsPan()
    {
        var view = new ZoomPanController();
        view.Pan(50f, -30f);

        // 2000x1000 image in a 1000x1000 viewport → width is the limiting axis (0.5).
        view.Fit(2000, 1000, 1000f, 1000f);

        Assert.Equal(0.5f, view.TargetZoom, 3);
        Assert.Equal(0f, view.TargetPanX);
        Assert.Equal(0f, view.TargetPanY);
    }

    [Fact]
    public void SetOneToOne_SetsZoomToExactlyOne()
    {
        var view = new ZoomPanController();
        view.Fit(4000, 3000, 1400f, 750f);
        view.SetOneToOne();

        Assert.Equal(1f, view.TargetZoom);
        Assert.True(view.IsOneToOne);
    }

    [Fact]
    public void ZoomIn_ClampsAtMaxZoom()
    {
        var view = new ZoomPanController();
        for (int i = 0; i < 200; i++) view.ZoomIn();

        Assert.Equal(ZoomPanController.MaxZoom, view.TargetZoom);
    }

    [Fact]
    public void ZoomOut_ClampsAtMinZoom()
    {
        var view = new ZoomPanController();
        for (int i = 0; i < 200; i++) view.ZoomOut();

        Assert.Equal(ZoomPanController.MinZoom, view.TargetZoom);
    }

    [Fact]
    public void ZoomAt_KeepsTheImagePointUnderTheCursorFixed()
    {
        var view = new ZoomPanController();
        view.Pan(37f, -12f);

        const float viewW = 800f, viewH = 600f;
        const float mouseX = 610f, mouseY = 145f;
        float mx = mouseX - viewW * 0.5f;
        float my = mouseY - viewH * 0.5f;

        // Image-space point currently under the cursor.
        float beforeX = (mx - view.TargetPanX) / view.TargetZoom;
        float beforeY = (my - view.TargetPanY) / view.TargetZoom;

        view.ZoomAt(+120f, mouseX, mouseY, viewW, viewH);

        float afterX = (mx - view.TargetPanX) / view.TargetZoom;
        float afterY = (my - view.TargetPanY) / view.TargetZoom;

        Assert.Equal(beforeX, afterX, 2);
        Assert.Equal(beforeY, afterY, 2);
    }

    [Fact]
    public void Update_ConvergesTowardTheTargets()
    {
        var view = new ZoomPanController();
        view.ZoomIn(); // target = 1.12

        for (int i = 0; i < 200; i++)
            view.Update(0.016f);

        Assert.Equal(1.12f, view.Zoom, 2);
    }

    [Fact]
    public void SnapToTargets_CopiesTargetsImmediately()
    {
        var view = new ZoomPanController();
        view.Fit(2000, 2000, 500f, 500f); // target zoom 0.25
        view.SnapToTargets();

        Assert.Equal(view.TargetZoom, view.Zoom);
        Assert.Equal(view.TargetPanX, view.PanX);
        Assert.Equal(view.TargetPanY, view.PanY);
    }

    [Fact]
    public void Pan_MovesBothCurrentAndTargetForImmediateDragResponse()
    {
        var view = new ZoomPanController();
        view.Pan(10f, 20f);

        Assert.Equal(10f, view.PanX);
        Assert.Equal(20f, view.PanY);
        Assert.Equal(10f, view.TargetPanX);
        Assert.Equal(20f, view.TargetPanY);
    }
}
