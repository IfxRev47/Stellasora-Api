using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Proto;

internal sealed class GameCatalog
{
    private readonly Dictionary<uint, uint> itemTypes;
    private readonly Dictionary<uint, uint> skinCharacters;
    public HashSet<uint> Heads { get; }

    public GameCatalog(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        itemTypes = json.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(x => x.GetProperty("id").GetUInt32(), x => x.GetProperty("type").GetUInt32());
        Heads = json.RootElement.GetProperty("heads").EnumerateArray().Select(x => x.GetUInt32()).ToHashSet();
        skinCharacters = json.RootElement.GetProperty("skins").EnumerateObject()
            .ToDictionary(x => uint.Parse(x.Name), x => x.Value.GetProperty("charId").GetUInt32());
    }

    public bool OwnsSkin(PlayerInfo player, uint skin) =>
        skinCharacters.TryGetValue(skin, out uint character) &&
        player.Chars.Any(x => x.Tid == character && (x.Skin == skin || skin == character * 100 + 1));

    public void NormalizeProfile(PlayerInfo player)
    {
        player.Acc ??= new AccInfo();
        if (!OwnsSkin(player, player.Acc.SkinId))
        {
            player.Acc.SkinId = player.Chars.SelectMany(x => new[] { x.Skin, x.Tid * 100 + 1 })
                .FirstOrDefault(x => OwnsSkin(player, x));
        }
        if (!Heads.Contains(player.Acc.HeadIcon)) player.Acc.HeadIcon = 101;
        // Config.NewPlayerTitlePrefix=1 / NewPlayerTitleSuffix=2.
        // FriendGridCtrl/FriendCarteCtrl dereference Title[0] without a guard.
        if (player.Acc.TitlePrefix == 0) player.Acc.TitlePrefix = 1;
        if (player.Acc.TitleSuffix == 0) player.Acc.TitleSuffix = 2;
        foreach (uint title in new uint[] { 1, 2 })
            if (!player.Titles.Any(x => x.TitleId == title)) player.Titles.Add(new Title { TitleId = title });
    }

    public ChangeInfo Grant(PlayerInfo player, IEnumerable<ItemTpl> rewards)
    {
        var change = new ChangeInfo();
        foreach (var reward in rewards)
        {
            if (reward.Qty <= 0 || !itemTypes.TryGetValue(reward.Tid, out uint type) || type is not (1 or 2))
                throw new GameRuleException("仅支持目录中的资源与普通背包道具");
            if (type == 1)
            {
                var res = player.Res.FirstOrDefault(x => x.Tid == reward.Tid);
                if (res is null) { res = new Res { Tid = reward.Tid }; player.Res.Add(res); }
                res.Qty = checked(res.Qty + reward.Qty);
                change.Props.Add(Any.Pack(new Res { Tid = reward.Tid, Qty = reward.Qty }));
            }
            else
            {
                var item = player.Items.FirstOrDefault(x => x.Tid == reward.Tid && x.Expire == 0);
                if (item is null)
                {
                    item = new Item { Tid = reward.Tid, Id = checked(player.Items.Select(x => x.Id).DefaultIfEmpty().Max() + 1) };
                    player.Items.Add(item);
                }
                item.Qty = checked(item.Qty + reward.Qty);
                change.Props.Add(Any.Pack(new Item { Id = item.Id, Tid = item.Tid, Qty = reward.Qty, Expire = item.Expire }));
            }
        }
        return change;
    }
}

internal sealed class GameRuleException(string message) : Exception(message);
