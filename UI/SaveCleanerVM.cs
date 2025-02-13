using TaleWorlds.Library;
using TaleWorlds.Localization;

// ReSharper disable UnusedMember.Global

namespace SaveCleaner.UI;

public class SaveCleanerVM : ViewModel
{
    private bool _isActive;
    private string _savingText;
    private TextObject _text;

    [DataSourceProperty]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (value == _isActive) return;
            _isActive = value;
            OnPropertyChangedWithValue(value);
        }
    }

    [DataSourceProperty]
    public string SavingText
    {
        get => _savingText;
        set
        {
            _savingText = value;
            OnPropertyChangedWithValue(value);
        }
    }

    internal TextObject Text
    {
        get => _text;
        set
        {
            _text = value;
            RefreshValues();
        }
    }

    public override void RefreshValues()
    {
        base.RefreshValues();
        SavingText = _text?.ToString() ?? "";
    }
}