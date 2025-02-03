using SandBox.View.Map;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Localization;
using TaleWorlds.ScreenSystem;

namespace SaveCleaner;

public class CleanerMapView : MapView
{
    private SaveCleanerVM DataSource { get; set; }

    protected override void CreateLayout()
    {
        base.CreateLayout();
        DataSource = new SaveCleanerVM();
        var layer = new GauntletLayer(99999);
        Layer = layer;
        layer.LoadMovie("MapSave", DataSource);
        Layer.InputRestrictions.ResetInputRestrictions();
        MapScreen.AddLayer(Layer);
    }

    internal void SetActive(bool active)
    {
        DataSource.IsActive = active;
        if (active)
        {
            Layer.IsFocusLayer = true;
            ScreenManager.TrySetFocus(Layer);
            Layer.InputRestrictions.SetInputRestrictions(false);
        }
        else
        {
            Layer.IsFocusLayer = false;
            ScreenManager.TryLoseFocus(Layer);
            Layer.InputRestrictions.ResetInputRestrictions();
        }
    }

    internal void SetText(TextObject text)
    {
        DataSource.Text = text;
    }

    protected override void OnFinalize()
    {
        base.OnFinalize();
        DataSource.OnFinalize();
        MapScreen.RemoveLayer(Layer);
        Layer = null;
        DataSource = null;
    }
}