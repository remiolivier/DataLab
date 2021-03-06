## Add ConfigTool Website to local IIS
## Requires IIS Administration Access, Import Now.
Import-Module WebAdministration -ErrorAction SilentlyContinue

## Variables
[string] $SiteName = "ConfigTool"
[string] $ConfigToolPath = (Split-Path -parent $MyInvocation.MyCommand.Definition) + '\Deployment\ConfigTool'
[int]    $Port = 8585  
[array]  $binding = @{protocol='http';bindingInformation=':'+$Port+':'}

## Step 1: Create Application Pool
New-Item IIS:\AppPools\$SiteName -Force
Set-ItemProperty IIS:\AppPools\$SiteName managedRuntimeVersion v4.0 -Force

## Step 2: Create Website
New-Item IIS:\Sites\$SiteName -bindings $Binding -PhysicalPath $ConfigToolPath -Force

## Step 3: Configure Website
Set-ItemProperty IIS:\Sites\$SiteName -Name applicationPool -Value $SiteName

## Step 4: Open the Browser
& "$env:programfiles\Internet Explorer\iexplore.exe" http://127.0.0.1:$Port