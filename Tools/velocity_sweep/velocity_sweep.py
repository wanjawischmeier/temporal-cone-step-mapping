#!/usr/bin/env python3
"""
Plot cone-step reprojection performance across different camera velocities
with evenly spaced measurement points.

Opens two windows:
  1. The original 3-panel velocity view (absolute steps, reduction %, GPU time),
     x-axis = speed/frame-count samples, evenly spaced regardless of their values.
  2. A margin-weight view in the same style as weights_sweep_1d.py (1D heatmap strip +
     line plot), but plotting step-count-reduction % instead of absolute step count,
     x-axis = margin_weight. Most useful when minFrames == maxFrames in the profiler, so
     margin_weight is the only thing actually varying across the sweep; if margin_weight
     was left fixed (min == max) this window will just show a flat line/strip.

Usage:
    python plot_cone_step_velocity.py cone_step_velocity_sweep.csv
"""
import argparse
import sys
import numpy as np
import matplotlib.pyplot as plt


def normalize(values, lo=None, hi=None):
    if lo is None:
        lo = values.min()
    if hi is None:
        hi = values.max()
    if hi - lo < 1e-12:
        return np.zeros_like(values)
    return np.clip((values - lo) / (hi - lo), 0, 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv_path", nargs="?", default=None, help="Path to the sweep CSV file")
    parser.add_argument("--raw", action="store_true",
        help="Color the margin-weight heatmap by raw reduction %% instead of normalized [0,1]")
    parser.add_argument("--percentile-clip", type=float, default=None, metavar="P",
        help="Clip margin-weight heatmap color scale to [P, 100-P] percentiles, e.g. --percentile-clip 2")
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
    margin_weight = data['margin_weight']
    on_steps = data['history_on_steps']
    off_steps = data['history_off_steps']
    on_time = data['history_on_time_ms']
    off_time = data['history_off_time_ms']

    # Sort by speed (or total_frames descending)
    order = np.argsort(speed)
    frames = frames[order]
    speed = speed[order]
    margin_weight = margin_weight[order]
    on_steps = on_steps[order]
    off_steps = off_steps[order]
    on_time = on_time[order]
    off_time = off_time[order]

    # Calculate step reduction percentage
    step_reduction = (1.0 - (on_steps / off_steps)) * 100.0

    # --- Window 1: original velocity view ---
    x_indices = np.arange(len(frames))
    x_labels = [f"{s:.3f}\n({f}f)" for s, f in zip(speed, frames)]

    fig1, axs = plt.subplots(3, 1, figsize=(10, 12), sharex=True)
    fig1.suptitle("Temporal Reprojection Performance vs. Camera Velocity", fontsize=14, y=0.96)

    axs[0].plot(x_indices, off_steps, marker='o', linestyle='-', color='red', label="History OFF (Baseline)")
    axs[0].plot(x_indices, on_steps, marker='s', linestyle='-', color='blue', label="History ON (Reprojected)")
    axs[0].set_ylabel("Total Cone Steps")
    axs[0].set_title("Absolute Step Count")
    axs[0].grid(True, alpha=0.3)
    axs[0].legend()

    axs[1].plot(x_indices, step_reduction, marker='D', linestyle='-', color='green', label="Step Reduction %")
    axs[1].axhline(0, color='black', linewidth=1, linestyle='--')
    axs[1].set_ylabel("Reduction (%)")
    axs[1].set_title("Relative Step Count Savings")
    axs[1].grid(True, alpha=0.3)
    axs[1].legend()

    axs[2].plot(x_indices, off_time, marker='o', linestyle='-', color='red', label="History OFF time (ms)")
    axs[2].plot(x_indices, on_time, marker='s', linestyle='-', color='blue', label="History ON time (ms)")
    axs[2].set_ylabel("GPU Time (ms)")
    axs[2].set_title("Total GPU Rendering Duration")
    axs[2].grid(True, alpha=0.3)
    axs[2].legend()

    axs[2].set_xticks(x_indices)
    axs[2].set_xticklabels(x_labels, rotation=0, fontsize=9)
    axs[2].set_xlabel("Normalized Speed (1 / Total Frames)")

    fig1.tight_layout(rect=[0, 0, 1, 0.95]) # type: ignore

    # --- Window 2: margin-weight view (weights_sweep_1d.py style), reduction % instead of steps ---
    margin_order = np.argsort(margin_weight)
    margin_sorted = margin_weight[margin_order]
    reduction_sorted = step_reduction[margin_order]

    lo = reduction_sorted.min()
    hi = reduction_sorted.max()

    display_values = reduction_sorted
    c_label = "step reduction (%)"

    if args.raw:
        display_values = reduction_sorted
        c_label = "step reduction (%)"
    elif args.percentile_clip is not None:
        lo = np.percentile(reduction_sorted, args.percentile_clip)
        hi = np.percentile(reduction_sorted, 100 - args.percentile_clip)
        display_values = normalize(reduction_sorted, lo, hi)
        c_label = f"step reduction (clipped {args.percentile_clip:.0f}-{100 - args.percentile_clip:.0f}%ile)"
    else:
        # display_values = normalize(reduction_sorted)
        display_values = reduction_sorted
        c_label = "step reduction (normalized 0-1)"

    best_idx = int(np.argmax(reduction_sorted))
    print(f"best margin weight (max step reduction): margin_weight={margin_sorted[best_idx]:.4f}, "
        f"reduction={reduction_sorted[best_idx]:.2f}%")

    fig2, (ax0, ax1) = plt.subplots(2, 1, figsize=(9, 6), gridspec_kw={"height_ratios": [1, 4]})
    fig2.suptitle("Cone-step margin-weight sweep - step reduction vs. margin weight")

    extent = [margin_sorted.min(), margin_sorted.max(), 0, 1]
    im = ax0.imshow(display_values[np.newaxis, :], origin="lower", extent=extent,
        aspect="auto", cmap="viridis", vmin=lo, vmax=hi)
    ax0.set_yticks([])
    ax0.set_xlabel("margin weight")
    ax0.axvline(margin_sorted[best_idx], color="red", linestyle="--", linewidth=1)
    fig2.colorbar(im, ax=ax0, orientation="vertical", label=c_label, pad=0.02)

    ax1.plot(margin_sorted, reduction_sorted, marker="o", markersize=3, linewidth=1.5, color='green')
    ax1.scatter([margin_sorted[best_idx]], [reduction_sorted[best_idx]], color="red", zorder=5, label="max reduction")
    ax1.axhline(0, color='black', linewidth=1, linestyle='--')
    ax1.set_xlabel("margin weight")
    ax1.set_ylabel("step reduction (%)")
    ax1.set_ylim(lo - 0.1, hi + 0.1)
    ax1.set_title("Step count reduction vs. margin weight")
    ax1.grid(True, alpha=0.3)
    ax1.legend()

    fig2.tight_layout()

    plt.show()


if __name__ == "__main__":
    main()