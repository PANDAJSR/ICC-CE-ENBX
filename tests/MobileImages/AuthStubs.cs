namespace ICC.CE.ENBX.Seewo.Models
{
    public sealed class SeewoAuthProfile
    {
        public string Uid { get; set; } = "";
        public string Token { get; set; } = "";
        public bool IsValid => Uid.Length > 0 && Token.Length > 0;
    }
}
namespace ICC.CE.ENBX.Seewo.Services
{
    public sealed class SeewoAuthManager
    {
        public event Action<Models.SeewoAuthProfile?>? AuthStateChanged;
        public Models.SeewoAuthProfile? CurrentProfile { get; private set; }
        public void Save(Models.SeewoAuthProfile? profile)
        {
            CurrentProfile = profile;
            AuthStateChanged?.Invoke(profile);
        }
    }
}
