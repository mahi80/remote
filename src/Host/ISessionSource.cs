using System;
using System.Threading.Tasks;

namespace Host
{
    public interface ISessionSource : IDisposable
    {
        string Passcode { get; set; }

        event Action<string> Logged;
        event Func<string, Task<bool>> ConsentRequested;
        event Action<string> SessionStarted;
        event Action SessionEnded;

        void Start();
        void Stop();
    }
}
