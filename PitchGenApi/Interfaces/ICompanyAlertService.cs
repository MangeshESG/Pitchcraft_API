using PitchGenApi.Model;

namespace PitchGenApi.Interfaces
{
    public interface ICompanyAlertService
    {
        Task SendUserRegisteredAlert(ClientDetails user, string ip, string browser);
        Task SendUserLoginAlert(ClientDetails user, string ip, string browser);
    }
}
