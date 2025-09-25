namespace AspNetWebApp.Options;

public class AzureBlobOptions
{
    public string? ConnectionString { get; set; }
    public string? ContainerName { get; set; }
    public string? StorageAccountName { get; set; }
}