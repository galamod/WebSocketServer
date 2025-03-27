using System.ComponentModel.DataAnnotations;

public class LicenseKey
{

    [Key]  // Первичный ключ
    public int Id { get; set; }

    [Required]
    public string Key { get; set; }

    [Required]
    public bool IsUnlimited { get; set; } = false;

    [Required]
    public string AppName { get; set; }  // Название приложения

    [Required]
    public DateTime ExpirationDate { get; set; }  // Дата истечения
}
