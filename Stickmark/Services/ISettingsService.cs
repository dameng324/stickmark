using System.Threading.Tasks;
using Stickmark.Models;

namespace Stickmark.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    Task SaveAsync();
}
