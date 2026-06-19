using System.ComponentModel.DataAnnotations;
using PassReset.Common;

namespace PassReset.Web.Models;

/// <summary>
/// Request DTO for a v2.1 Status Check: the user's current credentials plus an optional
/// reCAPTCHA token. Reuses the same enumeration-safe validation contract as the change flow.
/// </summary>
public class StatusCheckModel
{
    [Required(ErrorMessage = nameof(ApiErrorCode.FieldRequired))]
    [MaxLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = nameof(ApiErrorCode.FieldRequired))]
    [MaxLength(256)]
    public string CurrentPassword { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string Recaptcha { get; set; } = string.Empty;
}
