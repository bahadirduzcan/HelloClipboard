Clear-Host
Write-Host "-----------------------------------------------"
Write-Host "lm_commit_generator.ps1 (LM Studio Supported)"
Write-Host "Automatically generate a git commit message using LM Studio AI"
Write-Host "-----------------------------------------------`n"

# --- CONFIGURATION ---
# LM Studio default API endpoint (Full URL for Chat Completions)
$lmStudioApiUrl = "http://127.0.0.1:16449/v1/chat/completions"
# The exact model name loaded in LM Studio (e.g., "gpt-oss-20b")
$model = "gpt-oss-20b" 
# ----------------------

# Check: Ensure the LM Studio server is running!

# Create the payload in Chat format
function Invoke-LMStudioAI {
    param(
        [string]$Prompt,
        [string]$Model,
        [int]$MaxTokens = 1024
    )

    $body = @{
        model       = $Model
        messages    = @(
            @{ role = "system"; content = "You are a concise AI code analyst." }
            @{ role = "user"; content = $Prompt }
        )
        temperature = 0.3
        max_tokens  = $MaxTokens
        stream      = $false
    } | ConvertTo-Json -Depth 5

    try {
        # Send API request to LM Studio
        $response = Invoke-RestMethod -Uri $lmStudioApiUrl -Method Post -Body $body -ContentType "application/json" -TimeoutSec 180

        # Extract message content from the response
        if ($response.choices -and $response.choices.count -gt 0) {
            $message = $response.choices[0].message.content.Trim()
            return $message.Trim('"').Trim("'")
        }
        else {
            return $null
        }
    }
    catch {
        Write-Host "❌ API request failed: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

# Check if git is installed
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "❌ Git is not installed or not in PATH."
    exit
}

# Get unstaged and staged files
$unstaged = git diff --name-only
# Filter out empty lines AND exclude auto-generated or configuration files
$staged_files = git diff --cached --name-only | 
Where-Object { 
    $_ -ne "" -and 
    $_ -notlike "*.g.dart" -and 
    $_ -notlike "*.freezed.dart" -and
    $_ -notlike "*.csproj" -and 
    $_ -notlike "*.Designer.cs" 
}

# If nothing staged, ask user if they want to stage all
if (-not $staged_files) {
    if ($unstaged) {
        Write-Host "⚠️  No staged changes found, but there are unstaged files:`n"
        Write-Host $unstaged -ForegroundColor Yellow
        $stageAnswer = Read-Host "`nDo you want to stage ALL changes? (y/n)"
        if ($stageAnswer -eq "y") {
            git add -A
            Write-Host "`n✅ All changes staged."
            # Re-fetch staged files, applying the same filter
            $staged_files = git diff --cached --name-only | 
            Where-Object { 
                $_ -ne "" -and 
                $_ -notlike "*.g.dart" -and 
                $_ -notlike "*.freezed.dart" -and
                $_ -notlike "*.csproj" -and 
                $_ -notlike "*.Designer.cs" 
            }

            if (-not $staged_files) {
                Write-Host "`n⚠️ All files were staged, but only auto-generated files remained for processing. No files to analyze. Exiting." -ForegroundColor DarkYellow
                exit
            }
        }
        else {
            Write-Host "`n❌ No files staged. Exiting."
            exit
        }
    }
    else {
        Write-Host "⚠️  No modified or staged files found. Nothing to commit."
        exit
    }
}

Write-Host "`n🔍 Analyzing staged files with $model (LM Studio)...`n"

$display_output = "" # Holds the output to be displayed on screen

foreach ($file in $staged_files) {
    Write-Host "Processing file: **$file**" -ForegroundColor Yellow

    # Get diff content only for the current file
    $file_diff = git diff --cached -- "$file"

    if (-not $file_diff) {
        Write-Host "   ⚠️ Could not get staged diff for **$file**. Skipping." -ForegroundColor DarkYellow
        continue
    }

    # Prepare AI prompt
    # The System message is defined in the function, only diff content is sent here.
    $prompt = @"
Analyze the following git diff for a single file.

Generate ONE concise summary using Conventional Commits style:

<type>: <summary>

Allowed types:
- feat (new feature)
- fix (bug fix)
- refactor (code change without behavior change)
- perf (performance improvement)
- docs (documentation)
- test (tests)
- chore (tooling, config, maintenance)

Rules:
- Single line only
- Lowercase type
- Do NOT mention file names
- Do NOT add explanations
- Max 72 characters

Diff:
$file_diff
"@


    # Generate commit message using LM Studio API
    $ai_response = Invoke-LMStudioAI -Prompt $prompt -Model $model

    if (-not $ai_response) {
        $message = "❌ Failed to get response for **$file**. (API or Model Error)"
        Write-Host $message -ForegroundColor Red
        $display_output += "* **$file**: $message`n"
    }
    else {
        $message = $ai_response
        
        if ($message -notmatch '^(feat|fix|refactor|perf|docs|test|chore):\s+') {
            $message = "chore: $message"
        }

        Write-Host "   ✅ Result: $message" -ForegroundColor Green
        
        # Format for screen display
        $display_output += "* **$file**: $message`n"
    }
}

# Optionally, send the list back to LM Studio to generate a single summary message
Write-Host "`n🧠 Generating final summary commit message..."

$summary_context = $display_output
if ($summary_context.Length -gt 2000) {
    $summary_context = $summary_context.Substring(0, 2000)
}

$summary_prompt = @"
You are given a list of per-file change summaries below.

Generate ONE git commit message following Conventional Commits format:

<type>: <summary>

Allowed types:
- feat (new feature)
- fix (bug fix)
- refactor (code change without behavior change)
- perf (performance improvement)
- docs (documentation)
- test (tests)
- chore (tooling, config, maintenance)

Rules:
- Max 72 characters total
- Lowercase type
- Do NOT mention file names
- Do NOT add explanations
- Return ONLY the commit message

Changes:
$summary_context
"@

$summary_message = Invoke-LMStudioAI `
    -Prompt $summary_prompt `
    -Model $model `
    -MaxTokens 2048

# --- DISPLAY RESULTS ---
Write-Host "`n============================================"
Write-Host "  ✅ AI-GENERATED COMMIT ANALYSIS" -ForegroundColor Cyan
Write-Host "============================================"

Write-Host "`n## Per-File Analysis Results (Data Sent and Result Received)`n"
Write-Host $display_output

Write-Host "`n---"
Write-Host "## 📝 Suggested Main Commit Message (Summary)" -ForegroundColor Cyan
Write-Host $summary_message -ForegroundColor Green
Write-Host "---`n"

if (-not $summary_message) {
    $summary_message = "chore: update multiple files"
}

if ($summary_message -notmatch '^(feat|fix|refactor|perf|docs|test|chore):\s+') {
    $summary_message = "chore: $summary_message"
}

# Confirm commit
$answer = Read-Host "Do you want to commit with this summary message? (y/n)"
if ($answer -eq "y") {
    # Trim and remove quotes
    $commit_message = $summary_message.Trim('"')

    $commit_message = $commit_message.Trim()

    # Escape any remaining double quotes just in case
    $commit_message = $commit_message -replace '"', '\"'

    # Commit
    git commit -m "$commit_message"

    Write-Host "`n✅ Commit created successfully."

    # Ask to push
    $pushAnswer = Read-Host "`nDo you want to push the commit to remote? (y/n)"
    if ($pushAnswer -eq "y") {
        Write-Host "`n🚀 Pushing to remote..."
        git push
        if ($LASTEXITCODE -eq 0) {
            Write-Host "`n✅ Push completed successfully."
        }
        else {
            Write-Host "`n⚠️ Push failed. Please check your connection or credentials."
        }
    }
    else {
        Write-Host "`nPush skipped."
    }
}
else {
    Write-Host "`nCommit cancelled."
}