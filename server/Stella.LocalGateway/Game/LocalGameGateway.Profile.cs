using Proto;

internal sealed partial class LocalGameGateway
{
    private IResult EditName(HttpContext context, Session session, PlayerNameEditReq request)
    {
        if (!TryNormalizeNickname(request.Name, out string nickname, out _, out string reason)) throw new GameRuleException(reason);
        var player = playerStore.UpdatePlayer(session.Uid, player => player.Acc.NickName = nickname);
        return Reply(context, 1007, new PlayerNameEditResp { Hashtag = player.Acc.Hashtag, ResetTime = player.NicknameResetTime }, session.Key);
    }

    private IResult EditHead(HttpContext context, Session session, PlayerHeadIconSetReq request)
    {
        playerStore.UpdatePlayer(session.Uid, player =>
        {
            if (request.HeadIcon != player.Acc.HeadIcon) throw new GameRuleException("该头像尚未解锁");
            player.Acc.HeadIcon = request.HeadIcon;
        });
        return Reply(context, 1010, new Nil(), session.Key);
    }

    private IResult EditSignature(HttpContext context, Session session, PlayerSignatureEditReq request)
    {
        if (request.Signature.EnumerateRunes().Count() > 60 || request.Signature.Any(char.IsControl))
            throw new GameRuleException("签名最多 60 字，不能包含控制字符");
        playerStore.UpdatePlayer(session.Uid, player => player.Acc.Signature = request.Signature);
        return Reply(context, 1028, new Nil(), session.Key);
    }

    private IResult EditShowCharacters(HttpContext context, Session session, PlayerCharsShowReq request)
    {
        playerStore.UpdatePlayer(session.Uid, player =>
        {
            if (request.CharIds.Count > 3 || request.CharIds.Distinct().Count() != request.CharIds.Count ||
                request.CharIds.Any(id => player.Chars.All(x => x.Tid != id))) throw new GameRuleException("展示角色必须已拥有且不重复，最多 3 位");
            player.Acc.Chars.Clear();
            foreach (uint id in request.CharIds)
            {
                var character = player.Chars.First(x => x.Tid == id);
                player.Acc.Chars.Add(new CharShow { CharId = id, Level = character.Level, Skin = character.Skin });
            }
        });
        return Reply(context, 1034, new Nil(), session.Key);
    }

    private IResult EditShowSkin(HttpContext context, Session session, PlayerSkinShowReq request)
    {
        playerStore.UpdatePlayer(session.Uid, player =>
        {
            if (!playerStore.Catalog.OwnsSkin(player, request.SkinId)) throw new GameRuleException("皮肤无效或尚未拥有");
            player.Acc.SkinId = request.SkinId;
        });
        return Reply(context, 1037, new Nil(), session.Key);
    }

    private IResult ReplyOperationFailure(HttpContext context, Session session, short? id, string reason)
    {
        var error = new Error { Code = 110104 };
        error.Arguments.Add(reason);
        return id switch
        {
            1006 => Reply(context, 1008, error, session.Key),
            1009 => Reply(context, 1011, error, session.Key),
            1027 => Reply(context, 1029, error, session.Key),
            1033 => Reply(context, 1035, error, session.Key),
            1036 => Reply(context, 1038, error, session.Key),
            1057 => Reply(context, 1059, error, session.Key),
            1201 => Reply(context, 1203, error, session.Key),
            1204 => Reply(context, 1206, error, session.Key),
            1207 => Reply(context, 1209, error, session.Key),
            1210 => Reply(context, 1212, error, session.Key),
            1213 => Reply(context, 1215, error, session.Key),
            1216 => Reply(context, 1218, error, session.Key),
            1219 => Reply(context, 1221, error, session.Key),
            1222 => Reply(context, 1224, error, session.Key),
            1225 => Reply(context, 1227, error, session.Key),
            1228 => Reply(context, 1230, error, session.Key),
            1231 => Reply(context, 1233, error, session.Key),
            1234 => Reply(context, 1236, error, session.Key),
            9001 => Reply(context, 9003, error, session.Key),
            9004 => Reply(context, 9006, error, session.Key),
            9007 => Reply(context, 9009, error, session.Key),
            9010 => Reply(context, 9012, error, session.Key),
            9013 => Reply(context, 9015, error, session.Key),
            _ => Results.Problem(statusCode: 503, title: "存档操作失败"),
        };
    }
}
