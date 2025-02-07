#!/bin/bash

# Check if Directory.Packages.props exists
if [ ! -f "Directory.Packages.props" ]; then
    echo "Directory.Packages.props not found!"
    exit 1
fi

# Get all outdated packages across all projects
outdated=$(dotnet list package --outdated)

# Extract package information and update Directory.Packages.props
echo "$outdated" | grep ">" | while read -r line; do
    # Extract package name and latest version
    package=$(echo "$line" | awk '{print $2}')
    latest=$(echo "$line" | awk '{print $5}')
    
    if [ ! -z "$package" ] && [ ! -z "$latest" ]; then
        echo "Updating $package to version $latest"
        
        # Use sed to update the version in Directory.Packages.props
        # This handles both Version and VersionOverride attributes
        sed -i "s/<PackageVersion Include=\"$package\" Version=\"[^\"]*\"/<PackageVersion Include=\"$package\" Version=\"$latest\"/g" Directory.Packages.props
        sed -i "s/<PackageVersion Include=\"$package\" VersionOverride=\"[^\"]*\"/<PackageVersion Include=\"$package\" VersionOverride=\"$latest\"/g" Directory.Packages.props
    fi
done

echo "Package updates complete! Review Directory.Packages.props for changes."