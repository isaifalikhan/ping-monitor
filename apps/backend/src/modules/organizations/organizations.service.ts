import { Injectable, NotFoundException, ForbiddenException } from '@nestjs/common';
import { UserRole } from '@netwatch/shared';
import { PrismaService } from '../../prisma/prisma.service';
import { UpdateOrganizationDto } from './dto/update-organization.dto';

@Injectable()
export class OrganizationsService {
  constructor(private readonly prisma: PrismaService) {}

  async getOrganization(organizationId: string) {
    const org = await this.prisma.organization.findFirst({
      where: { id: organizationId, deletedAt: null },
    });

    if (!org) throw new NotFoundException('Organization not found');

    return {
      id: org.id,
      name: org.name,
      slug: org.slug,
      createdAt: org.createdAt,
    };
  }

  async updateOrganization(
    organizationId: string,
    userRole: UserRole,
    dto: UpdateOrganizationDto,
  ) {
    if (userRole !== UserRole.OWNER) {
      throw new ForbiddenException('Only owners can update organization settings');
    }

    const org = await this.prisma.organization.update({
      where: { id: organizationId },
      data: { name: dto.name },
    });

    return {
      id: org.id,
      name: org.name,
      slug: org.slug,
    };
  }

  async getTeamMembers(organizationId: string) {
    const members = await this.prisma.user.findMany({
      where: { organizationId, deletedAt: null },
      select: {
        id: true,
        email: true,
        firstName: true,
        lastName: true,
        role: true,
        emailVerified: true,
        isActive: true,
        lastLoginAt: true,
        createdAt: true,
      },
      orderBy: { createdAt: 'asc' },
    });

    const invitations = await this.prisma.teamInvitation.findMany({
      where: {
        organizationId,
        status: 'PENDING',
        expiresAt: { gt: new Date() },
      },
      select: {
        id: true,
        email: true,
        role: true,
        expiresAt: true,
        createdAt: true,
      },
    });

    return { members, pendingInvitations: invitations };
  }

  async updateMember(
    organizationId: string,
    memberId: string,
    actorRole: UserRole,
    actorId: string,
    dto: { role?: UserRole; isActive?: boolean },
  ) {
    if (actorRole === UserRole.VIEWER) {
      throw new ForbiddenException('Insufficient permissions');
    }

    const member = await this.prisma.user.findFirst({
      where: { id: memberId, organizationId, deletedAt: null },
    });
    if (!member) throw new NotFoundException('Member not found');

    if (member.id === actorId && dto.isActive === false) {
      throw new ForbiddenException('Cannot disable your own account');
    }

    if (dto.role === UserRole.OWNER && actorRole !== UserRole.OWNER) {
      throw new ForbiddenException('Only owners can assign owner role');
    }

    const updated = await this.prisma.user.update({
      where: { id: memberId },
      data: dto,
      select: {
        id: true,
        email: true,
        firstName: true,
        lastName: true,
        role: true,
        isActive: true,
      },
    });

    return updated;
  }

  async removeMember(
    organizationId: string,
    memberId: string,
    actorRole: UserRole,
    actorId: string,
  ) {
    if (actorRole === UserRole.VIEWER) {
      throw new ForbiddenException('Insufficient permissions');
    }
    if (memberId === actorId) {
      throw new ForbiddenException('Cannot remove your own account');
    }

    const member = await this.prisma.user.findFirst({
      where: { id: memberId, organizationId, deletedAt: null },
    });
    if (!member) throw new NotFoundException('Member not found');

    await this.prisma.user.update({
      where: { id: memberId },
      data: { deletedAt: new Date(), isActive: false },
    });

    return { message: 'Member removed' };
  }
}
