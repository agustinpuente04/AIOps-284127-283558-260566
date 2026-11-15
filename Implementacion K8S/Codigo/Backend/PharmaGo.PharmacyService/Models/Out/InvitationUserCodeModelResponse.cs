using System;
namespace PharmaGo.PharmacyService.Models.Out
{
	public class InvitationUserCodeModelResponse
	{
		public string UserCode { get; set; }

		public InvitationUserCodeModelResponse(string userCode)
		{
			this.UserCode = userCode;
		}
	}
}

