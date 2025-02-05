# Bannerlord Save Cleaner

A modular tool for cleaning up unused data and reducing save bloat in Bannerlord.

Supports add-ons for extended compatibility with other mods.

## Download
[Nexus](https://www.nexusmods.com/mountandblade2bannerlord/mods/7763/)

## For Modders

To make an addon for Save Cleaner, instantiate **[SaveCleanerAddon](https://github.com/JungleDruid/bannerlord-save-cleaner/blob/master/SaveCleanerAddon.cs)** and call `Register<SubModule>()`.

The public methods of the `SaveCleanerAddon` class are fully documented and should have everything you need.

Wrapping `Register<SubModule>()` in try-catch will allow your mod to start without SaveCleaner.

Also, make sure the reference in .csproj file is not [private](https://github.com/JungleDruid/BanditMilitias/blob/c7e7b61107df276f039599f3ffe27d0bc71a99f2/BanditMilitias.csproj#L47) so the `SaveCleaner.dll` won't be deployed with your mod.

### Samples

[Default Addon](https://github.com/JungleDruid/bannerlord-save-cleaner/blob/master/DefaultAddon.cs)

[Bandit Militia](https://github.com/JungleDruid/BanditMilitias/blob/master/SaveCleanerSupport.cs)
