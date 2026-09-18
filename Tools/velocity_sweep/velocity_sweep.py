#!/usr/bin/env python3
"""
Plot cone-step reprojection performance across different camera velocities
with evenly spaced measurement points.

Usage: 
    python plot_cone_step_velocity.py cone_step_velocity_sweep.csv
"""
import argparse
import sys
import numpy as np
import matplotlib.pyplot as plt

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv_path", nargs="?", default=None, help="Path to the sweep CSV file")
    args = parser.parse_args()

    # Prompt if not provided via command line
    if not args.csv_path:
        args.csv_path = input("Enter path to the sweep CSV file: ").strip()

    try:
        data = np.genfromtxt(args.csv_path, delimiter=',', names=True, dtype=float)
    except Exception as e:
        sys.exit(f"Failed to read {args.csv_path}: {e}")

    frames = data['total_frames'].astype(int)
    speed = data['speed']
    on_steps = data['history_on_steps']
    off_steps = data['history_off_steps']
    on_time = data['history_on_time_ms']
    off_time = data['history_off_time_ms']

    # Sort by speed (or total_frames descending)
    order = np.argsort(speed)
    frames = frames[order]
    speed = speed[order]
    on_steps = on_steps[order]
    off_steps = off_steps[order]
    on_time = on_time[order]
    off_time = off_time[order]

    # Calculate step reduction percentage
    step_reduction = (1.0 - (on_steps / off_steps)) * 100.0

    # Create categorical indices for even spacing along the x-axis
    x_indices = np.arange(len(frames))
    x_labels = [f"{s:.3f}\n({f}f)" for s, f in zip(speed, frames)]

    fig, axs = plt.subplots(3, 1, figsize=(10, 12), sharex=True)
    fig.suptitle("Temporal Reprojection Performance vs. Camera Velocity", fontsize=14, y=0.96)

    # Plot 1: Total Step Counts
    axs[0].plot(x_indices, off_steps, marker='o', linestyle='-', color='red', label="History OFF (Baseline)")
    axs[0].plot(x_indices, on_steps, marker='s', linestyle='-', color='blue', label="History ON (Reprojected)")
    axs[0].set_ylabel("Total Cone Steps")
    axs[0].set_title("Absolute Step Count")
    axs[0].grid(True, alpha=0.3)
    axs[0].legend()

    # Plot 2: Step Count Reduction %
    axs[1].plot(x_indices, step_reduction, marker='D', linestyle='-', color='green', label="Step Reduction %")
    axs[1].axhline(0, color='black', linewidth=1, linestyle='--')
    axs[1].set_ylabel("Reduction (%)")
    axs[1].set_title("Relative Step Count Savings")
    axs[1].grid(True, alpha=0.3)
    axs[1].legend()

    # Plot 3: Average Frametime Overhead
    axs[2].plot(x_indices, off_time, marker='o', linestyle='-', color='red', label="History OFF time (ms)")
    axs[2].plot(x_indices, on_time, marker='s', linestyle='-', color='blue', label="History ON time (ms)")
    # axs[2].set_ylabel("Total Dispatch Time (ms)")
    # axs[2].set_title("CPU Render Thread Duration (Approximate)")
    axs[2].set_ylabel("GPU Time (ms)")
    axs[2].set_title("Total GPU Rendering Duration")
    axs[2].grid(True, alpha=0.3)
    axs[2].legend()

    # Set custom evenly spaced x-axis ticks and labels
    axs[2].set_xticks(x_indices)
    axs[2].set_xticklabels(x_labels, rotation=0, fontsize=9)
    axs[2].set_xlabel("Normalized Speed (1 / Total Frames)")

    plt.tight_layout(rect=[0, 0, 1, 0.95])
    plt.show()

if __name__ == "__main__":
    main()