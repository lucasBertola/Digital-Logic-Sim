# Redige des notes de version lisibles a partir de la liste des commits, via l'API Claude.
# Utilise par publierRelease.bat. Ecrit OutFile seulement en cas de succes (sinon code de sortie 1 :
# le .bat garde alors la liste brute des commits).
#   releaseNotes.ps1 -CommitsFile <commits.txt> -OutFile <notes.md> -Title "Changements depuis V2" -Tag V3
param(
    [Parameter(Mandatory = $true)] [string] $CommitsFile,
    [Parameter(Mandatory = $true)] [string] $OutFile,
    [string] $Title = "Changements",
    [string] $Tag = ""
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$apiKey = [Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY', 'Machine')
if (-not $apiKey) { $apiKey = [Environment]::GetEnvironmentVariable('ANTHROPIC_API_KEY', 'User') }
if (-not $apiKey) { $apiKey = $env:ANTHROPIC_API_KEY }
if (-not $apiKey) { Write-Host "ANTHROPIC_API_KEY absente."; exit 1 }

$commits = [IO.File]::ReadAllText($CommitsFile, [Text.Encoding]::UTF8).Trim()
if (-not $commits) { Write-Host "Aucun commit."; exit 1 }

$system = @"
Tu rediges les notes de version d'un logiciel : Digital Logic Sim, un simulateur de circuits logiques (fork avec un assistant IA integre, des outils d'optimisation de circuits et des ameliorations d'editeur).
On te donne la liste des commits depuis la version precedente. Ecris des notes de version en francais, en Markdown, destinees aux utilisateurs du logiciel :
- regroupe par theme (Nouveautes, Ameliorations, Corrections...) avec un titre de niveau ## par theme, seulement les themes utiles ;
- une puce par changement visible par l'utilisateur, formulee du point de vue de l'utilisateur (ce que ca change pour lui), sans jargon de code, sans noms de fichiers, de classes ou de fonctions ;
- fusionne les commits qui parlent de la meme chose, ignore les commits purement internes (documentation de dev, refactoring, scripts de build) ou mentionne-les en une ligne a la fin ;
- reste concis : pas d'introduction, pas de conclusion, pas de formule de politesse. Uniquement le Markdown des notes.
"@

$body = @{
    model      = 'claude-opus-5'
    max_tokens = 4000
    system     = $system
    output_config = @{ effort = 'medium' }
    fallbacks  = 'default'
    messages   = @(@{
        role    = 'user'
        content = "Version : $Tag`n$Title`n`nCommits (sujet, puis corps eventuel) :`n`n$commits"
    })
}

$headers = @{
    'x-api-key'         = $apiKey
    'anthropic-version' = '2023-06-01'
    'anthropic-beta'    = 'server-side-fallback-2026-07-01'
    'content-type'      = 'application/json'
}

try {
    $json = $body | ConvertTo-Json -Depth 8
    $r = Invoke-RestMethod -Method Post -Uri 'https://api.anthropic.com/v1/messages' -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($json)) -ContentType 'application/json; charset=utf-8'
}
catch {
    Write-Host "Appel API echoue : $($_.Exception.Message)"
    exit 1
}

if ($r.stop_reason -eq 'refusal') { Write-Host "Reponse refusee par l'API."; exit 1 }
$text = ($r.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n"
if (-not $text.Trim()) { Write-Host "Reponse vide."; exit 1 }

[IO.File]::WriteAllText($OutFile, $text.Trim() + "`n", (New-Object Text.UTF8Encoding($false)))
exit 0
