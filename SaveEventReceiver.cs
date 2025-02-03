using System;
using TaleWorlds.CampaignSystem;

namespace SaveCleaner;

public class SaveEventReceiver : CampaignEventReceiver
{
    public event Action<bool, string> SaveOver;

    public override void OnSaveOver(bool isSuccessful, string saveName)
    {
        SaveOver?.Invoke(isSuccessful, saveName);
    }
}