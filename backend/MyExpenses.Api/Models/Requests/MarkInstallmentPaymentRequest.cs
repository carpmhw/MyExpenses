namespace MyExpenses.Api.Models.Requests;

public class MarkInstallmentPaymentRequest
{
    /// <summary>實際繳款日（不包含時間）</summary>
    public DateOnly? PaidDate { get; set; }
}
