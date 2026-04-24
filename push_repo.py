import os
import subprocess

# Configuration
PAT = os.environ.get("GITHUB_PAT")
REPO_URL = f"https://dinglenutsxnex-crypto:{PAT}@github.com/dinglenutsxnex-crypto/Mmm.git"
BRANCH = "main"
COMMIT_MESSAGE = "Update repository"

def run_command(cmd):
    """Run a shell command and return output"""
    result = subprocess.run(cmd, shell=True, capture_output=True, text=True)
    print(result.stdout)
    if result.stderr:
        print("Error:", result.stderr)
    return result.returncode == 0

def push_to_github():
    """Push changes to GitHub"""
    os.chdir(r"c:\Users\Admin\Downloads\S-UAW-v3-fixed\Mmm")
    
    print("Adding files...")
    run_command("git add .")
    
    print("Committing changes...")
    run_command(f'git commit -m "{COMMIT_MESSAGE}"')
    
    print("Setting remote URL with credentials...")
    run_command(f"git remote set-url origin {REPO_URL}")
    
    print("Pushing to GitHub...")
    success = run_command(f"git push origin {BRANCH}")
    
    if success:
        print("✓ Successfully pushed to GitHub!")
    else:
        print("✗ Failed to push to GitHub")

if __name__ == "__main__":
    push_to_github()
