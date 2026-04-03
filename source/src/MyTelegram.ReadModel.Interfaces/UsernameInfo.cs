namespace MyTelegram.ReadModel.Interfaces;

/// <summary>
/// Username information including Fragment collectible status
/// </summary>
public class UsernameInfo
{
    /// <summary>
    /// The username (without @)
    /// </summary>
    public string Username { get; set; } = default!;
    
    /// <summary>
    /// Whether the username is editable (true = basic username, false = Fragment collectible)
    /// </summary>
    public bool Editable { get; set; }
    
    /// <summary>
    /// Whether the username is active (visible to others)
    /// </summary>
    public bool Active { get; set; }
}
