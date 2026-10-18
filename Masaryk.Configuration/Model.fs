namespace Masaryk.Configuration

[<RequireQualifiedAccess>]
type SourceMode =
    | Whitelist
    | Blacklist

type Board =
    {
        Name: string
        Mode: SourceMode
        Channels: Set<uint64>
        Target: uint64
        Threshold: int
        IsActive: bool
    }

type MenuSource =
    /// A restaurant page on menicka.cz.
    | Menicka of id: int
    /// A restaurant in the lepsimenu.cz API.
    | LepsiMenu of id: string
    /// A Wolt venue; only menu categories whose slug contains `category` count.
    | Wolt of slug: string * category: string
    /// Táo's own weekly menu page.
    | Tao
    /// No menu online, just a link.
    | Link

type Restaurant =
    {
        Name: string
        Icon: string
        Color: uint32
        Url: string
        Source: MenuSource
    }

/// Everything a server's admins can change about the bot.
type Configuration =
    {
        /// Where the bot reports progress and problems to admins.
        LogChannel: uint64 option
        Boards: Board list
        /// Courses of this faculty get channel names without a faculty prefix.
        HomeFaculty: string option
        Restaurants: Restaurant list
    }
