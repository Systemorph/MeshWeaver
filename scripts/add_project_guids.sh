#!/bin/bash

# Script to add ProjectGuid elements to all .csproj files in a solution

# Function to generate a random GUID in the format {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
generate_guid() {
    # Generate a GUID without using uuidgen
    local hex_chars="0123456789abcdef"
    local uuid=""
    
    # Format: 8-4-4-4-12
    for i in {1..8}; do
        uuid="${uuid}${hex_chars:$(( RANDOM % 16 )):1}"
    done
    uuid="${uuid}-"
    
    for i in {1..4}; do
        uuid="${uuid}${hex_chars:$(( RANDOM % 16 )):1}"
    done
    uuid="${uuid}-"
    
    # Set version 4 UUID (random)
    uuid="${uuid}4"
    for i in {1..3}; do
        uuid="${uuid}${hex_chars:$(( RANDOM % 16 )):1}"
    done
    uuid="${uuid}-"
    
    # Set variant
    local variant=$(( 8 + RANDOM % 4 ))
    uuid="${uuid}${hex_chars:${variant}:1}"
    
    for i in {1..3}; do
        uuid="${uuid}${hex_chars:$(( RANDOM % 16 )):1}"
    done
    uuid="${uuid}-"
    
    for i in {1..12}; do
        uuid="${uuid}${hex_chars:$(( RANDOM % 16 )):1}"
    done
    
    echo "{$uuid}"
}

# Find all .csproj files in the current directory and subdirectories
find . -name "*.csproj" -type f | while read -r project_file; do
    echo "Processing $project_file"
    
    # Check if ProjectGuid already exists in the file
    if grep -q "<ProjectGuid>" "$project_file"; then
        echo "  ProjectGuid already exists in $project_file"
    else
        # Generate a new GUID
        new_guid=$(generate_guid)
        
        # Create a temporary file
        temp_file=$(mktemp)
        
        # Check if there's already a PropertyGroup
        if grep -q "<PropertyGroup>" "$project_file"; then
            # Find the first PropertyGroup tag and add the ProjectGuid after it
            awk -v guid="$new_guid" '
                /<PropertyGroup>/ {
                    print $0
                    print "    <ProjectGuid>" guid "</ProjectGuid>"
                    found=1
                    next
                }
                {print}
            ' "$project_file" > "$temp_file"
        else
            # If no PropertyGroup exists, add one after the Project tag
            awk -v guid="$new_guid" '
                /<Project / {
                    print $0
                    print "  <PropertyGroup>"
                    print "    <ProjectGuid>" guid "</ProjectGuid>"
                    print "  </PropertyGroup>"
                    next
                }
                {print}
            ' "$project_file" > "$temp_file"
            echo "  No PropertyGroup found, adding one after Project tag"
        fi
        
        # Copy the temp file back to the original
        cp "$temp_file" "$project_file"
        rm "$temp_file"
        
        echo "  Added ProjectGuid: $new_guid to $project_file"
    fi
done

echo "Finished processing all project files"