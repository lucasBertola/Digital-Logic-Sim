# Redige des notes de version lisibles a partir de la liste des commits, et choisit le numero de version
# suivant d'apres l'importance des changements (V<majeur>.<mineur>.<correctif>), via l'API Claude.
# Utilise par publierRelease.bat. Ecrit OutFile (notes) et VersionFile (numero propose) seulement en cas de
# succes (sinon code de sortie 1 : le .bat garde la liste brute des commits et sa propre proposition).
#   releaseNotes.ps1 -CommitsFile <commits.txt> -OutFile <notes.md> -VersionFile <version.txt> -LastTag V1.2.0
param(
    [Parameter(Mandatory = $true)] [string] $CommitsFile,
    [Parameter(Mandatory = $true)] [string] $OutFile,
    [Parameter(Mandatory = $true)] [string] $VersionFile,
    [string] $LastTag = ""
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
- fusionne les commits qui parlent de la meme chose ; si un commit annule ou retire ce qu'un commit precedent avait ajoute, ne mentionne ni l'un ni l'autre (l'utilisateur ne voit que le resultat final) ; ignore les commits purement internes (documentation de dev, refactoring, scripts de build) ou mentionne-les en une ligne a la fin ;
- reste concis : pas d'introduction, pas de conclusion, pas de formule de politesse.
Tu choisis aussi le numero de la nouvelle version, au format V<majeur>.<mineur>.<correctif> :
- s'il n'y a pas de version precedente : V1.0.0 ;
- sinon, a partir de la version precedente : +1 sur le correctif si les changements sont seulement des corrections ou des details, +1 sur le mineur (correctif remis a 0) s'il y a des nouveautes ou des ameliorations visibles, +1 sur le majeur (mineur et correctif remis a 0) si l'ensemble est une evolution importante du logiciel ou si des projets sauvegardes pourraient ne plus s'ouvrir a l'identique.
Reponds en JSON : {"version": "Vx.y.z", "notes": "<le Markdown des notes, sans titre de version>"}.
"@

$schema = @{
    type = 'object'
    additionalProperties = $false
    required = @('version', 'notes')
    properties = @{
        version = @{ type = 'string'; description = 'Nouveau numero de version, ex: V1.2.0' }
        notes   = @{ type = 'string'; description = 'Notes de version en Markdown' }
    }
}

$body = @{
    model      = 'claude-opus-5'
    max_tokens = 4000
    system     = $system
    output_config = @{ effort = 'medium'; format = @{ type = 'json_schema'; schema = $schema } }
    fallbacks  = 'default'
    messages   = @(@{
        role    = 'user'
        content = "Version precedente : $(if ($LastTag) { $LastTag } else { '(aucune, premiere version)' })`n`nCommits depuis cette version (sujet, puis corps eventuel) :`n`n$commits"
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

try { $result = $text | ConvertFrom-Json } catch { Write-Host "Reponse JSON invalide."; exit 1 }
if (-not $result.version -or $result.version -notmatch '^V\d+\.\d+\.\d+$' -or -not $result.notes) { Write-Host "Reponse incomplete : $text"; exit 1 }

$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($OutFile, $result.notes.Trim() + "`n", $utf8)
[IO.File]::WriteAllText($VersionFile, $result.version.Trim(), $utf8)
Write-Host ("Version proposee par Claude : " + $result.version)
exit 0
